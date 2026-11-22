using KeePassXC.CloudTransport;

namespace KeyPassXCCloudSync;

/// <summary>
/// Branded, self-contained HTML for the package-owned loopback OAuth callback.
/// Only fixed translations and theme colors are interpolated: never codes, tokens,
/// account names, callback URLs, or third-party resources.
/// </summary>
internal static class GoogleOAuthCallbackPage
{
    internal static GoogleOAuthCallbackPages Create(string language, bool darkTheme)
        => new(Generate(language, darkTheme, succeeded: true),
               Generate(language, darkTheme, succeeded: false));

    // Kept independent of the Desktop project so Core and the headless tests can
    // produce the same result pages. The caller supplies the resolved UI language.
    internal static string Generate(string language, bool darkTheme, bool succeeded)
    {
        var lang = language is "ru" or "uk" or "en" ? language : "en";
        var headline = succeeded ? lang switch
        {
            "ru" => "Авторизация завершена",
            "uk" => "Авторизацію завершено",
            _ => "Authorization complete"
        } : lang switch
        {
            "ru" => "Авторизация не завершена",
            "uk" => "Авторизацію не завершено",
            _ => "Authorization not completed"
        };
        var description = succeeded ? lang switch
        {
            "ru" => "Аккаунт Google Drive успешно подключён к KeyPassXC Cloud Sync.",
            "uk" => "Обліковий запис Google Drive успішно підключено до KeyPassXC Cloud Sync.",
            _ => "Your Google Drive account is connected to KeyPassXC Cloud Sync."
        } : lang switch
        {
            "ru" => "Не удалось подтвердить подключение. Вернитесь в программу и повторите попытку.",
            "uk" => "Не вдалося підтвердити підключення. Поверніться до програми й повторіть спробу.",
            _ => "The connection could not be confirmed. Return to the application and try again."
        };
        var instruction = succeeded ? lang switch
        {
            "ru" => "Можно закрыть эту вкладку и вернуться в программу.",
            "uk" => "Можна закрити цю вкладку й повернутися до програми.",
            _ => "You can close this tab and return to the application."
        } : lang switch
        {
            "ru" => "Закройте эту вкладку и проверьте настройки подключения.",
            "uk" => "Закрийте цю вкладку та перевірте налаштування підключення.",
            _ => "Close this tab and check your connection settings."
        };
        var state = succeeded ? lang switch
        {
            "ru" => "ПОДКЛЮЧЕНО", "uk" => "ПІДКЛЮЧЕНО", _ => "CONNECTED"
        } : lang switch
        {
            "ru" => "НЕ ПОДКЛЮЧЕНО", "uk" => "НЕ ПІДКЛЮЧЕНО", _ => "NOT CONNECTED"
        };
        var footer = lang switch
        {
            "ru" => "Защищённое локальное завершение входа",
            "uk" => "Безпечне локальне завершення входу",
            _ => "Secure local sign-in completion"
        };
        var background = darkTheme ? "#10151e" : "#eef3fa";
        var surface = darkTheme ? "#1a2433" : "#ffffff";
        var surface2 = darkTheme ? "#151e2b" : "#f6f9fd";
        var foreground = darkTheme ? "#eff5ff" : "#162338";
        var subtle = darkTheme ? "#a4b3c9" : "#52637e";
        var outline = darkTheme ? "#334254" : "#d9e2ed";
        var accent = succeeded ? "#48ccad" : "#ffa870";
        var resultPath = succeeded ? "M6 12.5l4.3 4.3L18.5 8.5" : "M12 7.5v5.5m0 4h.01";
        var colorScheme = darkTheme ? "dark" : "light";

        // Double-dollar interpolation leaves ordinary CSS braces literal.
        return $$"""
<!doctype html>
<html lang="{{lang}}">
<head>
  <meta charset="utf-8">
  <meta name="viewport" content="width=device-width, initial-scale=1">
  <meta name="referrer" content="no-referrer">
  <meta name="color-scheme" content="{{colorScheme}}">
  <title>KeyPassXC Cloud Sync · Google Drive</title>
  <style>
    :root { color-scheme: {{colorScheme}}; }
    * { box-sizing: border-box; }
    body { margin:0; min-height:100vh; display:grid; place-items:center; padding:24px;
      font-family:"Segoe UI",Inter,system-ui,-apple-system,sans-serif; color:{{foreground}};
      background:radial-gradient(ellipse at 18% 14%,rgba(68,148,196,.17),transparent 43%),
      radial-gradient(ellipse at 82% 84%,rgba(72,204,173,.09),transparent 45%),{{background}}; }
    main { width:min(100%,550px); padding:35px 35px 25px; background:{{surface}};
      border:1px solid {{outline}}; border-radius:18px;
      box-shadow:0 25px 80px rgba(0,0,0,.22); }
    .brand { display:flex; align-items:center; gap:15px; margin-bottom:31px; }
    .appmark { display:grid; place-items:center; width:51px; height:51px; border-radius:13px;
      background:{{surface2}}; border:1px solid {{outline}}; }
    .brandname { font-size:18px; font-weight:670; letter-spacing:-.35px; }
    .brandinfo { color:{{subtle}}; font-size:12px; margin-top:4px; }
    .rule { height:1px; background:{{outline}}; margin:0 -35px 29px; }
    .result { display:flex; align-items:center; gap:12px; margin-bottom:13px; }
    .resulticon { width:43px; height:43px; flex-shrink:0; display:grid; place-items:center;
      border-radius:50%; color:{{accent}}; border:1px solid {{accent}};
      background:{{surface2}}; }
    .status { font-size:11px; font-weight:750; letter-spacing:1.5px; color:{{accent}}; }
    h1 { font-size:24px; letter-spacing:-.65px; line-height:1.25; margin:0 0 12px; }
    p { font-size:14px; color:{{subtle}}; line-height:1.65; margin:0; }
    .hint { margin-top:25px; padding:14px 16px; border:1px solid {{outline}};
      background:{{surface2}}; border-radius:10px; font-size:13px; color:{{foreground}}; }
    footer { display:flex; align-items:center; gap:8px; margin-top:23px;
      color:{{subtle}}; font-size:11px; }
    footer svg { color:{{accent}}; flex-shrink:0; }
    @media(max-width:540px) { main { padding:26px 22px 22px; } .rule { margin-left:-22px; margin-right:-22px; } h1 { font-size:21px; } }
  </style>
</head>
<body>
<main role="main">
  <div class="brand">
    <div class="appmark" aria-hidden="true">
      <svg width="31" height="32" viewBox="0 0 32 32" fill="none" stroke="currentColor" stroke-width="1.65" stroke-linecap="round" stroke-linejoin="round">
        <path d="M8 20a7 7 0 0 1 2.1-13.6 9 9 0 0 1 16 4.5A6 6 0 0 1 24 23H9"/>
        <path d="M12 17l4 4 7-8"/>
      </svg>
    </div>
    <div><div class="brandname">KeyPassXC Cloud Sync</div><div class="brandinfo">Google Drive · OAuth 2.0</div></div>
  </div>
  <div class="rule"></div>
  <div class="result">
    <div class="resulticon" aria-hidden="true">
      <svg width="25" height="25" viewBox="0 0 24 24" fill="none" stroke="currentColor" stroke-width="2.3" stroke-linecap="round" stroke-linejoin="round"><path d="{{resultPath}}"/></svg>
    </div>
    <span class="status">{{state}}</span>
  </div>
  <h1>{{headline}}</h1>
  <p>{{description}}</p>
  <div class="hint">{{instruction}}</div>
  <footer>
    <svg width="15" height="15" viewBox="0 0 24 24" fill="none" stroke="currentColor" stroke-width="1.6"><rect x="5" y="10" width="14" height="11" rx="2"/><path d="M8 10V7a4 4 0 0 1 8 0v3"/></svg>
    {{footer}}
  </footer>
</main>
</body>
</html>
""";
    }
}
