using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using System.Windows;
using System.Windows.Markup;

namespace SetupLauncher.App
{
    /// <summary>
    /// 负责从嵌入资源里取出 XAML 并用 XamlReader 在运行时解析。
    ///
    /// 这样做是为了绕开 XAML 编译步骤（这台机器上没有 MSBuild / XAML 编译器），
    /// 代价是界面事件必须在 C# 里手工挂接，XAML 里不能写 Click= 这类特性。
    /// </summary>
    public static class Xaml
    {
        private static readonly Dictionary<string, string> Cache = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        private const string AsmPlaceholder = "__ASM__";

        /// <summary>读取嵌入的 XAML 源文本。</summary>
        public static string Source(string logicalName)
        {
            string cached;
            if (Cache.TryGetValue(logicalName, out cached)) return cached;

            var asm = typeof(Xaml).Assembly;
            var name = logicalName;
            if (!name.EndsWith(".xaml", StringComparison.OrdinalIgnoreCase)) name += ".xaml";

            Stream stream = asm.GetManifestResourceStream(name);
            if (stream == null)
            {
                // 资源逻辑名可能被加了命名空间前缀，退化成模糊匹配
                foreach (var candidate in asm.GetManifestResourceNames())
                {
                    if (candidate.EndsWith(name, StringComparison.OrdinalIgnoreCase))
                    {
                        stream = asm.GetManifestResourceStream(candidate);
                        break;
                    }
                }
            }
            if (stream == null)
                throw new InvalidOperationException("找不到嵌入资源 XAML：" + logicalName);

            using (stream)
            using (var reader = new StreamReader(stream, Encoding.UTF8))
            {
                var text = reader.ReadToEnd();

                // 松散 XAML 没有程序集上下文，clr-namespace 必须显式写出 assembly=。
                // XAML 里用 __ASM__ 占位，这里替换成真实程序集名，避免改名后失效。
                if (text.IndexOf(AsmPlaceholder, StringComparison.Ordinal) >= 0)
                    text = text.Replace(AsmPlaceholder, asm.GetName().Name);

                Cache[logicalName] = text;
                return text;
            }
        }

        /// <summary>解析出对象树。</summary>
        public static object Parse(string logicalName)
        {
            return System.Windows.Markup.XamlReader.Parse(Source(logicalName));
        }

        /// <summary>加载主题资源字典。</summary>
        public static ResourceDictionary LoadTheme()
        {
            return (ResourceDictionary)Parse("Theme");
        }
    }
}
