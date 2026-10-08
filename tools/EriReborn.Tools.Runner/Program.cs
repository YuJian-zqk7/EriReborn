using System.Collections.ObjectModel;
using System.Diagnostics;
using System.Globalization;
using System.Reflection;
using System.Text;

namespace EriReborn.Tools.Runner;

/// <summary>
/// Runs a test assembly directly, without vstest.
///
/// vstest's testhost tries to watch its parent process and dies with an access
/// denial on this machine (CLR exception 0xe0434352 before a single test runs).
/// This runner loads the test assembly in-process and invokes the test methods
/// itself, so the suite stays runnable and its results stay honest.
///
/// It deliberately takes no dependency on xunit: attributes are recognised by
/// type name, and assertion failures are recognised by namespace. That keeps the
/// runner working even if the test project's packages change.
/// </summary>
internal static class Program
{
    private static async Task<int> Main(string[] args)
    {
        if (args.Length == 0 || args[0] is "-h" or "--help")
        {
            Console.WriteLine("用法: EriReborn.Tools.Runner <测试程序集.dll> [--filter <子串>] [--log <路径>]");
            return args.Length == 0 ? 2 : 0;
        }

        var assemblyPath = Path.GetFullPath(args[0]);
        if (!File.Exists(assemblyPath))
        {
            Console.WriteLine($"找不到测试程序集: {assemblyPath}");
            return 2;
        }

        var filter = Value(args, "--filter");
        var logPath = Value(args, "--log");
        var withStack = args.Contains("--stack");

        Console.WriteLine($"程序集 : {assemblyPath}");
        Console.WriteLine($"过滤器 : {filter ?? "(无)"}");

        // Probe the assembly's own folder first: that is where its dependencies
        // (xunit, the product assemblies) actually sit.
        var directory = Path.GetDirectoryName(assemblyPath)!;
        AppDomain.CurrentDomain.AssemblyResolve += (_, eventArgs) =>
        {
            var name = new AssemblyName(eventArgs.Name).Name + ".dll";
            var candidate = Path.Combine(directory, name);
            return File.Exists(candidate) ? Assembly.LoadFrom(candidate) : null;
        };

        var assembly = Assembly.LoadFrom(assemblyPath);
        var results = new List<TestResult>();

        foreach (var type in assembly.GetTypes().Where(IsTestClass).OrderBy(t => t.FullName, StringComparer.Ordinal))
        {
            foreach (var method in type.GetMethods(BindingFlags.Public | BindingFlags.Instance))
            {
                var fact = FindAttribute(method, "FactAttribute");
                var theory = FindAttribute(method, "TheoryAttribute");

                if (fact is null && theory is null)
                {
                    continue;
                }

                var name = $"{type.Name}.{method.Name}";
                if (filter is not null && !name.Contains(filter, StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                var skip = AttributeValue(fact ?? theory, "Skip");
                if (!string.IsNullOrEmpty(skip))
                {
                    results.Add(new TestResult(name, Outcome.Skipped, $"声明跳过: {skip}", 0));
                    continue;
                }

                if (theory is not null)
                {
                    await RunTheoryAsync(type, method, name, results, withStack).ConfigureAwait(false);
                }
                else
                {
                    results.Add(await RunOneAsync(type, method, name, Array.Empty<object?>(), withStack).ConfigureAwait(false));
                }
            }
        }

        Report(results, logPath);
        return results.Any(r => r.Outcome == Outcome.Failed) ? 1 : 0;
    }

    /// <summary>A concrete class with at least one runnable method.</summary>
    private static bool IsTestClass(Type type)
        => type is { IsClass: true, IsAbstract: false, IsGenericTypeDefinition: false }
           && !type.IsNested
           && type.FullName is not null;

    private static async Task RunTheoryAsync(Type type, MethodInfo method, string name, List<TestResult> results, bool withStack)
    {
        var cases = method.GetCustomAttributesData()
            .Where(data => data.AttributeType.Name == "InlineDataAttribute")
            .Select(ReadInlineData)
            .ToList();

        if (cases.Count == 0)
        {
            // MemberData and the like are not expanded here. Saying so is better
            // than pretending the case passed.
            results.Add(new TestResult(
                name,
                Outcome.Skipped,
                "本运行器只展开 [InlineData]；该 Theory 使用其它数据源。",
                0));
            return;
        }

        var index = 0;
        foreach (var arguments in cases)
        {
            var caseName = arguments.Length == 0 ? name : $"{name}({string.Join(", ", arguments.Select(Render))})";
            results.Add(await RunOneAsync(type, method, caseName, arguments, withStack).ConfigureAwait(false));
            index++;
        }
    }

    /// <summary>
    /// Reads one [InlineData] case.
    ///
    /// The attribute takes <c>params object[]</c>, so reflection reports a single
    /// constructor argument holding the whole array. Reading it as a scalar would
    /// pass the collection itself as the test's first parameter.
    /// </summary>
    private static object?[] ReadInlineData(CustomAttributeData data)
    {
        if (data.ConstructorArguments.Count != 1)
        {
            return data.ConstructorArguments.Select(argument => argument.Value).ToArray();
        }

        return data.ConstructorArguments[0].Value is ReadOnlyCollection<CustomAttributeTypedArgument> elements
            ? elements.Select(element => element.Value).ToArray()
            : new object?[] { data.ConstructorArguments[0].Value };
    }

    private static async Task<TestResult> RunOneAsync(
        Type type,
        MethodInfo method,
        string name,
        object?[] arguments,
        bool withStack)
    {
        var stopwatch = Stopwatch.StartNew();
        object? instance = null;

        try
        {
            instance = Activator.CreateInstance(type);

            var returned = method.Invoke(instance, arguments);
            if (returned is Task task)
            {
                await task.ConfigureAwait(false);
            }
            else if (returned is ValueTask valueTask)
            {
                await valueTask.ConfigureAwait(false);
            }

            return new TestResult(name, Outcome.Passed, null, stopwatch.ElapsedMilliseconds);
        }
        catch (Exception exception)
        {
            var detail = Describe(exception);
            if (withStack)
            {
                detail += Environment.NewLine + "      " + (exception.StackTrace ?? "(无堆栈)");
            }

            return new TestResult(name, Outcome.Failed, detail, stopwatch.ElapsedMilliseconds);
        }
        finally
        {
            if (instance is IDisposable disposable)
            {
                try
                {
                    disposable.Dispose();
                }
                catch
                {
                    // A failing Dispose must not hide the test's own result.
                }
            }
        }
    }

    /// <summary>Unwraps TargetInvocationException so the real failure is reported.</summary>
    private static string Describe(Exception exception)
    {
        if (exception is TargetInvocationException { InnerException: { } inner })
        {
            exception = inner;
        }

        var isAssertion = exception.GetType().Namespace?.StartsWith("Xunit.Sdk", StringComparison.Ordinal) == true;
        var head = isAssertion ? "断言失败" : $"异常 {exception.GetType().Name}";

        return $"{head}: {exception.Message}";
    }

    private static void Report(List<TestResult> results, string? logPath)
    {
        var failed = results.Where(r => r.Outcome == Outcome.Failed).ToList();
        var skipped = results.Where(r => r.Outcome == Outcome.Skipped).ToList();
        var passed = results.Count(r => r.Outcome == Outcome.Passed);

        Console.WriteLine();
        foreach (var result in results.Where(r => r.Outcome != Outcome.Passed))
        {
            Console.WriteLine($"{(result.Outcome == Outcome.Failed ? "FAIL" : "SKIP")}  {result.Name}  [{result.Milliseconds} ms]");
            if (result.Detail is not null)
            {
                Console.WriteLine($"      {result.Detail}");
            }
        }

        Console.WriteLine();
        Console.WriteLine($"已通过 {passed} / 失败 {failed.Count} / 跳过 {skipped.Count} / 合计 {results.Count}");

        // A machine-readable line, in ASCII, so a script never has to match
        // localised human text to decide whether the run was green. Parsing the
        // Chinese summary broke the moment a script's own literals were decoded
        // differently from the runner's output.
        Console.WriteLine($"TOTAL passed={passed} failed={failed.Count} skipped={skipped.Count} total={results.Count}");

        if (logPath is null)
        {
            return;
        }

        // The log format the specification asks for (spec 22): one row per test
        // with the result, the environment, the time and the detail.
        var log = new StringBuilder();
        log.AppendLine("# EriReborn 测试日志");
        log.AppendLine();
        log.AppendLine($"时间戳   : {DateTimeOffset.Now:yyyy-MM-dd HH:mm:ss zzz}");
        log.AppendLine($"操作系统 : {Environment.OSVersion}");
        log.AppendLine($"运行时   : {Environment.Version}");
        log.AppendLine($"机器     : {Environment.MachineName}");
        log.AppendLine($"处理器数 : {Environment.ProcessorCount}");
        log.AppendLine($"结论     : 通过 {passed} / 失败 {failed.Count} / 跳过 {skipped.Count}");
        log.AppendLine();
        log.AppendLine("测试结果 | 环境 | 时间戳 | 详情");
        log.AppendLine("---|---|---|---");

        foreach (var result in results)
        {
            var outcome = result.Outcome switch
            {
                Outcome.Passed => "PASS",
                Outcome.Failed => "FAIL",
                _ => "SKIP",
            };

            log.AppendLine(string.Join(
                " | ",
                outcome,
                result.Name,
                $"{result.Milliseconds} ms",
                result.Detail ?? string.Empty));
        }

        File.WriteAllText(logPath, log.ToString(), Encoding.UTF8);
        Console.WriteLine($"日志已写入: {Path.GetFullPath(logPath)}");
    }

    private static object? FindAttribute(MethodInfo method, string attributeName)
        => method.GetCustomAttributes().FirstOrDefault(a => a.GetType().Name == attributeName);

    private static string? AttributeValue(object? attribute, string propertyName)
        => attribute?.GetType().GetProperty(propertyName)?.GetValue(attribute) as string;

    private static string? Value(string[] args, string option)
    {
        var index = Array.IndexOf(args, option);
        return index >= 0 && index + 1 < args.Length ? args[index + 1] : null;
    }

    private static string Render(object? value) => value switch
    {
        null => "null",
        string text => $"\"{text}\"",
        _ => Convert.ToString(value, CultureInfo.InvariantCulture) ?? "?",
    };

    private enum Outcome
    {
        Passed,
        Failed,
        Skipped,
    }

    private sealed record TestResult(string Name, Outcome Outcome, string? Detail, long Milliseconds);
}
