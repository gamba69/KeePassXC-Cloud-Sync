using System.Text.RegularExpressions;

namespace KeyPassXCCloudSync;

// Presentation-only error classification. Windows and provider exception messages
// can use the OS locale even when the user selected another application language.
// Stored status, diagnostic reports and exception objects remain unchanged.
internal static class UiErrorDisplay
{
    private static string L(string ru,string uk,string en) => UiLang.Active switch
    {
        "uk" => uk, "en" => en, _ => ru
    };

    internal static string Present(Exception error)
        => Present(error.GetType().Name+": "+error.Message);

    internal static string Present(string? raw)
    {
        if(string.IsNullOrWhiteSpace(raw))return "";
        var original=raw.Trim();
        const string topologyPrefix="Проверка топологии: ";
        if(original.StartsWith(topologyPrefix,StringComparison.Ordinal))
            return UiLang.T(topologyPrefix)+Present(original[topologyPrefix.Length..]);
        if(original=="An incompatible cloud identity or version metadata was detected; publication stopped")
            return L("Несовместимый идентификатор облака или метаданные версии; публикация остановлена",
                "Несумісний ідентифікатор хмари або метадані версії; публікацію зупинено",
                original);
        if(original=="No cloud confirmed the current version")
            return L("Ни одно облако не подтвердило текущую версию",
                "Жодна хмара не підтвердила поточну версію",original);
        if(original=="Independent versions: manual KeePassXC Merge required")
            return L("Независимые версии: требуется ручное объединение в KeePassXC",
                "Незалежні версії: потрібне ручне об’єднання у KeePassXC",original);

        // The exception name is stable; the message is localized by .NET or
        // Windows. Never try to translate arbitrary text, paths or cloud names.
        var kind=Regex.Match(original,@"\b[A-Za-z][A-Za-z0-9]*Exception(?=\s*:)",
            RegexOptions.CultureInvariant).Value;
        var code=Classify(original);
        if(code is not null)
        {
            var description=code switch
            {
                "timeout" => L("Сервер не ответил вовремя", "Сервер не відповів вчасно", "Server response timed out"),
                "refused" => L("Сервер отклонил соединение", "Сервер відхилив з’єднання", "Server refused the connection"),
                "dns" => L("Не удалось определить адрес сервера", "Не вдалося визначити адресу сервера", "Could not resolve server address"),
                "offline" => L("Сервер или сеть недоступны", "Сервер або мережа недоступні", "Server or network unreachable"),
                "tls" => L("Не удалось установить защищённое соединение", "Не вдалося встановити захищене з’єднання", "Secure connection failed"),
                "auth" => L("Доступ отклонён сервером", "Сервер відхилив доступ", "Access denied by server"),
                "rate" => L("Сервер ограничил частоту запросов", "Сервер обмежив частоту запитів", "Server rate limit reached"),
                "file" => L("Файл не найден", "Файл не знайдено", "File not found"),
                "permission" => L("Недостаточно прав доступа", "Недостатньо прав доступу", "Insufficient permissions"),
                _ => L("Операция завершилась ошибкой", "Операція завершилася з помилкою", "Operation failed")
            };
            return kind.Length>0 ? description+" ("+kind+")" : description;
        }

        var translated=UiLang.T(original);
        // The ordinary dictionary translates known safety and provider messages.
        // Unknown OS text is handled below instead of escaping unlocalized.
        if(!string.Equals(translated,original,StringComparison.Ordinal) &&
           (UiLang.Active=="ru" || !Regex.IsMatch(translated,@"[\u0400-\u04FF]",
               RegexOptions.CultureInvariant) || UiLang.Active=="uk"))
        {
            // In Ukrainian, a prefix-only translation may retain the original
            // Russian Windows error; callers must use explicit classification.
            if(UiLang.Active!="uk" || !ContainsRussianOnlyText(translated))return translated;
        }

        // Raw English diagnostics are meaningful on an English UI, just as
        // Russian diagnostics are meaningful on a Russian UI. In any other
        // locale, show a localized safe summary rather than the OS-language
        // exception body. Detailed original errors remain in diagnostics.
        var cyrillic=Regex.IsMatch(original,@"[\u0400-\u04FF]",RegexOptions.CultureInvariant);
        if(UiLang.Active=="ru" && cyrillic)return original;
        if(UiLang.Active=="en" && !cyrillic)return original;
        return L("Ошибка операции", "Помилка операції", "Operation failed")+
            (kind.Length>0?" ("+kind+")":"");
    }

    private static bool ContainsRussianOnlyText(string text)
        =>text.Contains("т.к.",StringComparison.OrdinalIgnoreCase) ||
          text.Contains("не удалось",StringComparison.OrdinalIgnoreCase) ||
          text.Contains("ошибка соединения",StringComparison.OrdinalIgnoreCase);

    private static string? Classify(string error)
    {
        // Windows socket message text varies by installed OS language and
        // .NET version. Recognize high-confidence fragments and error codes.
        var s=error.ToLowerInvariant();
        if(Has(s,"10060","0x8007274c","попытка установить соединение была безуспешной",
            "connection attempt failed","timed out","истекло время ожидания","час очікування минув",
            "operation has timed out"))return "timeout";
        if(Has(s,"10061","0x8007274d","actively refused","connection refused",
            "отверг запрос на подключение","соединение не установлено, т.к. конечный компьютер отверг запрос",
            "відхилив запит на підключення"))return "refused";
        if(Has(s,"11001","0x80072af9","no such host is known","name or service not known",
            "не удалось разрешить удаленное имя","не удалось разрешить удалённое имя",
            "неизвестен такой хост","имя узла не найдено"))return "dns";
        if(Has(s,"10051","10065","network is unreachable","host is unreachable",
            "сеть недоступна","узел недоступен"))return "offline";
        if(Has(s,"authenticationexception","ssl connection could not be established",
            "certificate validation","сертификат","сертифікат"))return "tls";
        if(Has(s,"http 401","http 403","response status code does not indicate success: 401",
            "response status code does not indicate success: 403"))return "auth";
        if(Has(s,"http 429","response status code does not indicate success: 429"))return "rate";
        if(Has(s,"filenotfoundexception"))return "file";
        if(Has(s,"unauthorizedaccessexception","access to the path is denied"))return "permission";
        return null;
    }

    private static bool Has(string source,params string[] needles)
        =>needles.Any(needle=>source.Contains(needle,StringComparison.Ordinal));
}
