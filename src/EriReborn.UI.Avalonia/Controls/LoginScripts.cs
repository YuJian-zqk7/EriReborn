namespace EriReborn.UI.Avalonia.Controls;

/// <summary>
/// The script we inject into a cloud platform's page so the user never sees the official site.
///
/// <para>
/// It hides everything, then reveals only the chain of elements that matters: the password field
/// when the form is in the top document, otherwise the sign-in <c>iframe</c> that hosts it. Both
/// Baidu and 123 put their form in a cross-origin frame, which is why looking for the password field
/// alone never matched and the page stayed untouched.
/// </para>
///
/// <para>
/// When neither is present yet, it clicks the most likely sign-in entry once and says so, so the
/// caller can retry once the form appears. Clicking repeatedly toggles the form shut instead.
/// </para>
///
/// <para>
/// A cross-origin frame cannot be restyled from here, so its interior keeps whatever the platform
/// drew — that is a browser security boundary, not a limitation this code can lift. What we can and
/// do remove is the official site around it: navigation, banners, footers and site branding.
/// </para>
/// </summary>
public static class LoginScripts
{
    /// <summary>Isolates the sign-in form or its frame on the current page.</summary>
    public const string IsolateLoginForm = @"
(function () {
  function hideEverything() {
    var all = document.querySelectorAll('body *');
    for (var i = 0; i < all.length; i++) { all[i].style.visibility = 'hidden'; }
    document.body.style.visibility = 'visible';
    document.body.style.background = '#f6f6fb';
    document.body.style.margin = '0';
    document.body.style.overflow = 'hidden';
  }

  function showChain(node) {
    var n = node;
    while (n && n !== document.body) { n.style.visibility = 'visible'; n = n.parentElement; }
  }

  function showSubtree(node) {
    var all = node.querySelectorAll('*');
    for (var i = 0; i < all.length; i++) { all[i].style.visibility = 'visible'; }
  }

  function diag() {
    return ' title=' + String(document.title || '').slice(0, 28)
      + ' frames=' + document.querySelectorAll('iframe').length
      + ' pw=' + document.querySelectorAll('input[type=password]').length;
  }

  function frameHeight() { return Math.max(360, Math.min(660, window.innerHeight - 80)); }
  function frameWidth() { return Math.max(320, Math.min(560, window.innerWidth - 80)); }

  function looksLikeCaptcha(el) {
    var s = (el.className || '') + ' ' + (el.id || '') + ' ' + (el.getAttribute('alt') || '');
    return /captcha|verify|vcode|code-img/i.test(String(s));
  }

  function hideBrand(scope) {
    var marks = scope.querySelectorAll('[class*=logo],[id*=logo],img[src*=logo],[class*=brand],[class*=header-logo]');
    for (var i = 0; i < marks.length; i++) {
      if (!looksLikeCaptcha(marks[i])) { marks[i].style.visibility = 'hidden'; }
    }
  }

  // 1) the form is in this document

  var password = document.querySelector('input[type=password]');
  if (password) {
    var rect = password.getBoundingClientRect();
    if ((password.offsetParent === null || rect.width === 0) && !window.__eriUserTabClicked) {
      var tabs = document.querySelectorAll('a,span,li,div,button');
      for (var t = 0; t < tabs.length; t++) {
        var text = String(tabs[t].textContent || '').trim();
        if (text === '用户名登录' || text === '账号密码登录') {
          window.__eriUserTabClicked = true;
          tabs[t].click();
          return 'clicked-username-tab' + diag();
        }
      }
    }

    // Choose the box by SIZE, not by content: walk up from the password field and keep the last
    // ancestor that still occupies less than 40% of the viewport. Content-based guessing stopped at
    // the username form, which hid the (default) QR panel and left an empty card; the size rule
    // stops at the whole login card instead, site chrome excluded.
    var area = window.innerWidth * window.innerHeight * 0.4;
    var box = password;
    var node = password;
    while (node && node !== document.body) {
      var r = node.getBoundingClientRect();
      if (r.width * r.height > area) { break; }
      box = node;
      node = node.parentElement;
    }

    hideEverything();
    showChain(box);
    showSubtree(box);
    box.style.background = 'transparent';
    box.style.boxShadow = 'none';
    return 'stripped box=' + String(box.tagName) + diag();
  }

  // 2) the form lives in a sign-in frame
  var frame = document.querySelector(
    'iframe[src*=login],iframe[src*=passport],iframe[src*=Login],iframe[id*=login],iframe[class*=login]');
  if (frame) {
    hideEverything();
    showChain(frame);
    frame.style.visibility = 'visible';
    frame.style.position = 'fixed';
    frame.style.left = '50%';
    frame.style.top = '50%';
    frame.style.transform = 'translate(-50%, -50%)';
    frame.style.width = frameWidth() + 'px';
    frame.style.height = frameHeight() + 'px';
    frame.style.border = '0';
    frame.style.zIndex = '2147483647';
    return 'stripped-iframe' + diag();
  }

  // 3) open the sign-in entry, but only once per page
  if (!window.__eriLoginClicked) {
    var entry = document.querySelector(
      'a[href*=login],a[href*=passport],[class*=login-btn],[class*=loginBtn],[class*=login-button],[id*=login-btn]');
    if (entry) {
      window.__eriLoginClicked = true;
      entry.click();
      return 'clicked-login' + diag();
    }
  }

  return 'no-login-found' + diag();
})()";
}