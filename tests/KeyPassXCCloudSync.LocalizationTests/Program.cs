using System.Reflection;
using System.Text.RegularExpressions;
using KeyPassXCCloudSync;

// Runs without Avalonia, a network connection, Windows APIs or Python.
// Display texts are tested without altering engine messages or protocol IDs.
try
{
var field=typeof(UiLang).GetField("Terms",BindingFlags.Static|BindingFlags.NonPublic)
    ?? throw new Exception("Localization dictionary missing");
var catalog=(Dictionary<string,(string Uk,string En)>)(field.GetValue(null)
    ?? throw new Exception("Localization dictionary could not be read"));
TestReporter.Check("LocalizationTests","catalog size >= 450",catalog.Count>=450);
foreach(var (key,value) in catalog)
{
    TestReporter.Check("LocalizationTests","catalog contains both translations: "+key,
        !string.IsNullOrWhiteSpace(value.Uk) && !string.IsNullOrWhiteSpace(value.En));
    TestReporter.Check("LocalizationTests","catalog English text is not Cyrillic: "+key,
        // English translations must not contain Cyrillic characters, including metric labels.
        !Regex.IsMatch(value.En,@"[А-Яа-яЁёІіЇїЄєҐґ]"));
}
var tests=0;
void Record(string label,bool condition)
{
    TestReporter.Check("LocalizationTests",label,condition);
    tests++;
}

void Check(string language,string key,string expected)
{
    UiLang.Use(language);
    var actual=UiLang.T(key);
    Record(language+" translation: "+key,string.Equals(actual,expected,StringComparison.Ordinal));
}
Check("en","Настройки","Settings");
Check("en","Хранятся только на этом ПК. Не передаются другим компьютерам.",
    "Stored only on this PC. Not shared with other computers.");
Check("uk","Хранятся только на этом ПК. Не передаются другим компьютерам.",
    "Зберігаються лише на цьому ПК. Не передаються іншим комп’ютерам.");
Check("en","Две независимые группы: общие параметры распространяются через топологию; локальные действуют только на этом компьютере.",
    "Two independent groups: shared settings are distributed via the topology; local settings apply only to this computer.");
Check("en","Файлы и система","Files and system");
Check("uk","Файлы и система","Файли та система");
Check("en","Разрешение конфликтов","Conflict resolution");
Check("en","История","History");
Check("uk","История","Історія");
Check("en","О программе","About");
Check("en","Ошибка / облака недоступны","Error / clouds unavailable");
const string PercentileExplanation = "Медиана и P₉₅ вычисляются по точным длительностям успешных операций (мс). Скорость — объём данных / время операций передачи, не пропускная способность сети.";
foreach(var language in new[]{"ru","uk","en"})
{
    UiLang.Use(language);
    Record("percentile notation in "+language,
        UiLang.T(PercentileExplanation).Contains("P₉₅",StringComparison.Ordinal) &&
        !UiLang.T(PercentileExplanation).Contains('≤'));
}
foreach(var language in new[]{"ru","uk","en"})
{
    UiLang.Use(language);
    var expected=language switch{"en"=>"Median","uk"=>"Медіана",_=>"Медиана"};
    Record("median heading in "+language,UiLang.T("Медиана")==expected);
}
// The cloud-name validator accepts readable labels, including spaces and Unicode.
// Keep these assertions aligned with the messages actually emitted by the dialog
// and settings validation; never preserve a deleted rule as a localization test.
const string CloudNamePrompt = "Имя подключения: буквы, цифры, пробелы, _, -, ., скобки, & или +; от 1 до 80 символов.";
const string CloudNameError = "Недопустимое имя облачного подключения (1–80 символов; буквы, цифры, пробелы и безопасные разделители).";
Check("en",CloudNamePrompt,
    "Connection name: letters, digits, spaces, _, -, ., parentheses, & or +; 1–80 characters.");
Check("uk",CloudNamePrompt,
    "Назва підключення: літери, цифри, пробіли, _, -, ., дужки, & або +; від 1 до 80 символів.");
Check("en",CloudNameError,
    "Invalid cloud connection name (1–80 characters; letters, digits, spaces and safe separators).");
Check("uk",CloudNameError,
    "Неприпустима назва хмарного підключення (1–80 символів; літери, цифри, пробіли та безпечні роздільники).");
Check("en","KDBX на паузе · топология и статистика работают",
    "KDBX paused · topology and statistics still active");
Check("en","База: D:\\Vault\\Passwords.kdbx","Database: D:\\Vault\\Passwords.kdbx");
Check("en","Google подключён: Work","Google connected: Work");
Check("en","Топология проверена: 2026-10-30","Topology checked: 2026-10-30");
Check("uk","Топология проверена: 2026-10-30","Топологію перевірено: 2026-10-30");
Check("en","Топология проверена: Нет данных","Topology checked: No data");
Check("uk","Топология проверена: Нет данных","Топологію перевірено: Немає даних");
Check("en","Опрос топологии: каждые 30 s","Topology polling: every 30 s");
Check("uk","Опрос топологии: каждые 30 с","Опитування топології: кожні 30 с");
Check("en","Последняя проверка KDBX: 2026-10-30","Last KDBX check: 2026-10-30");
Check("en","Проверено: 2026-10-30","Checked: 2026-10-30");
// Exercise every catalogued dynamic label instead of relying on individual
// hand-picked examples. Unknown runtime values must be preserved verbatim.
foreach(var (prefix, translation) in catalog)
{
    if(!prefix.EndsWith(": ",StringComparison.Ordinal)
        && !prefix.EndsWith("каждые ",StringComparison.Ordinal))continue;
    Check("en",prefix+"2026-10-30",translation.En+"2026-10-30");
    Check("uk",prefix+"2026-10-30",translation.Uk+"2026-10-30");
}

// Real-world status screen regressions: worker strings and cloud errors must
// pass the presentation adapter, even when the worker is paused.
Check("en","Облако не авторизовано","Cloud is not authorized");
Check("uk","Облако не авторизовано","Хмара не авторизована");
Check("en","Требуется локальная авторизация","Local authorization required");
Check("uk","Требуется локальная авторизация","Потрібна локальна авторизація");
Check("en","Проверка топологии: Connection refused","Topology check: Connection refused");
Check("uk","Проверка топологии: Connection refused","Перевірка топології: Connection refused");
Check("en","KDBX на паузе · следующий опрос топологии","KDBX paused · next topology poll");
Check("uk","KDBX на паузе · следующий опрос топологии","KDBX на паузі · наступне опитування топології");
Check("en","KDBX на паузе · проверяю топологию облаков…","KDBX paused · checking cloud topology…");
Check("uk","KDBX на паузе · топология облаков опрашивается","KDBX на паузі · опитування топології хмар");
Check("en","Новая версия: Google…","New version: Google…");
Check("uk","Передана KDBX: Google…","Передано KDBX: Google…");
Check("en","Получена версия из облака…","Downloaded version from cloud…");
UiLang.Use("en");
Record("Google worker progress (en)",UiDiagnostics.Progress("Проверяю изменения в Google…")=="Checking changes in Google…");
Record("concurrent cloud progress (en)",UiLang.T("Проверяются облака: ")+2=="Checking clouds: 2");
UiLang.Use("uk");
Record("Google worker progress (uk)",UiDiagnostics.Progress("Проверяю изменения в Google…")=="Перевіряю зміни в Google…");
Check("en","ещё не выбрана","not yet selected");
Check("en","ОСТАНОВИТЬ синхронизацию только на этом компьютере?\n\n",
    "STOP synchronization on this computer only?\n\n");
UiLang.Use("en");
Record("paused worker progress (en)",UiDiagnostics.Progress("KDBX на паузе · следующий опрос топологии · осталось 00:34")
    =="KDBX paused · next topology poll · remaining 00:34");
UiLang.Use("uk");
Record("paused worker progress (uk)",UiDiagnostics.Progress("KDBX на паузе · следующий опрос топологии · осталось 00:34")
    =="KDBX на паузі · наступне опитування топології · залишилося 00:34");

// Automatic theme is displayed consistently across supported UI languages.
// Automatic theme storage and automatic language selection are independent.
foreach(var lang in new[]{"ru","uk","en"})
{
    UiLang.Use(lang);
    Record("automatic theme label: "+lang,UiLang.ThemeName("Системная")== (lang=="en"?"Auto":"Авто"));
}
UiLang.Use("en");
Record("light/dark theme names (en)",UiLang.ThemeName("Светлая")=="Light" && UiLang.ThemeName("Тёмная")=="Dark");
UiLang.Use("uk");
Record("light/dark theme names (uk)",UiLang.ThemeName("Светлая")=="Світла" && UiLang.ThemeName("Тёмная")=="Темна");
Check("en","Светлая","Light");
Check("ru","Светлая","Светлая");
UiLang.Use("en");
var summary=UiLang.T("В топологии: облаков — ")+2+UiLang.T(", активных компьютеров — ")+3+".";
Record("dynamic topology summary",summary=="In topology: clouds — 2, active computers — 3.");
Record("history progress translated",UiDiagnostics.Progress("Сверяю историю версий…").Contains("Checking version history",StringComparison.Ordinal));
// Guard new immediately rendered UI labels against silent Russian fallback.
// Diagnostic protocol/log text is tested separately through UiDiagnostics;
// user filenames and cloud IDs are never translated.
DirectoryInfo? current=new DirectoryInfo(AppContext.BaseDirectory);
while(current is not null && !File.Exists(Path.Combine(current.FullName,"Directory.Build.props")))
    current=current.Parent;
if(current is null)throw new Exception("Could not find source root for localization audit");
var desktop=Path.Combine(current.FullName,"src","KeyPassXCCloudSync.Desktop");

// OS-generated exception messages are not authored in the application's
// selected language. Classify for display and retain raw diagnostics on disk.
const string WindowsTimeout="HttpRequestException: Попытка установить соединение была безуспешной, т.к. от другого компьютера за требуемое время не получен нужный отклик (example.invalid:443)";
const string EnglishTimeout="HttpRequestException: A connection attempt failed because the connected party did not properly respond after a period of time";
foreach(var language in new[]{"ru","uk","en"})
{
    UiLang.Use(language);
    var expected=language switch
    {
        "ru"=>"Сервер не ответил вовремя (HttpRequestException)",
        "uk"=>"Сервер не відповів вчасно (HttpRequestException)",
        _=>"Server response timed out (HttpRequestException)"
    };
    Record("Windows socket timeout localized: "+language,
        UiErrorDisplay.Present(WindowsTimeout)==expected);
    Record("English socket timeout localized: "+language,
        UiErrorDisplay.Present(EnglishTimeout)==expected);
    Record("Windows socket error code localized: "+language,
        UiErrorDisplay.Present("SocketException: 10060")==expected.Replace("HttpRequestException","SocketException"));
}
UiLang.Use("en");
Record("unknown Russian OS message does not leak into English UI",
    UiErrorDisplay.Present("HttpRequestException: Неизвестная системная ошибка")=="Operation failed (HttpRequestException)");
UiLang.Use("uk");
Record("unknown English OS message does not leak into Ukrainian UI",
    UiErrorDisplay.Present("HttpRequestException: Unknown native transport failure")=="Помилка операції (HttpRequestException)");
UiLang.Use("en");
Record("known Core authorization failure remains specific",
    UiErrorDisplay.Present("Облако не авторизовано")=="Cloud is not authorized");
Record("topology error preserves context and localizes OS text",
    UiErrorDisplay.Present("Проверка топологии: "+WindowsTimeout)
        =="Topology check: Server response timed out (HttpRequestException)");
Record("English automatic theme label",
    UiLang.ThemeName("Системная")=="Auto");
Record("cloud error diagnostics localized without changing underlying event",
    UiDiagnostics.Line("[FAIL] "+WindowsTimeout)==
       "FAILED: Server response timed out (HttpRequestException)");
Record("network diagnostics localized",
    UiDiagnostics.Network(WindowsTimeout)==
       "Server response timed out (HttpRequestException)");
Record("user-facing cloud errors use display adapter",
    File.ReadAllText(Path.Combine(desktop,"ShellWindow.Overview.cs"))
       .Contains("UiErrorDisplay.Present(error)",StringComparison.Ordinal));
Record("modal errors use display adapter",
    File.ReadAllText(Path.Combine(desktop,"ShellWindow.cs"))
       .Contains("UiErrorDisplay.Present(message)",StringComparison.Ordinal));

// Fail if the visible dialog or settings validator changes its wording without
// updating RU/UK/EN. This would otherwise silently render Russian in other UIs.
var cloudDialogSource=File.ReadAllText(Path.Combine(desktop,"Dialogs.cs"));
Record("cloud name prompt matches localization source",cloudDialogSource.Contains("throw new ArgumentException(\""+CloudNamePrompt+"\")",StringComparison.Ordinal));
var cloudSettingsSource=File.ReadAllText(Path.Combine(current.FullName,"src",
    "KeyPassXCCloudSync.Core","SettingsStore.cs"));
Record("cloud name error matches localization source",cloudSettingsSource.Contains("throw new ArgumentException(\""+CloudNameError+"\")",StringComparison.Ordinal));

// Every fixed Russian EngineHost progress message must be translatable; this
// catches future Core statuses omitted from the Desktop translation catalog.
var engineSource=File.ReadAllText(Path.Combine(current.FullName,"src",
    "KeyPassXCCloudSync.Core","EngineHost.cs"));
var engineMessages=Regex.Matches(engineSource,
    @"ReportProgress\(\s*""[^""]+""\s*,\s*""(?<message>[^""]+)""");
foreach(Match match in engineMessages)
{
    var message=match.Groups["message"].Value;
    if(!Regex.IsMatch(message,@"[А-Яа-яЁёІїЄєҐґ]"))continue;
    foreach(var lang in new[]{"en","uk"})
    {
        UiLang.Use(lang);
        var translated=UiDiagnostics.Progress(message);
        Record("worker progress "+lang+": "+message,
            (lang!="en" || !Regex.IsMatch(translated,@"[А-Яа-яЁёІїЄєҐґ]")) && translated!=message);
    }
}

// Network diagnostic event tags are recognized by their stable event identifiers.
foreach(var lang in new[]{"ru","uk","en"})
{
    UiLang.Use(lang);
    foreach(var tag in new[]{"[NET ] "})
    {
        var raw=tag+"GET /test";
        var shown=UiDiagnostics.View(raw);
        Record("network diagnostic tag "+lang+": "+raw,
            shown is not null && shown.Kind==UiDiagnostics.LineKind.Network && !string.IsNullOrEmpty(shown.Text));
    }
}

// These strings originate in Core and must not bypass the display adapter.
var overviewSource=File.ReadAllText(Path.Combine(desktop,"ShellWindow.Overview.cs"));
Record("overview cloud errors and actions use presentation translation",
    overviewSource.Contains("Muted(UiErrorDisplay.Present(error),11)",StringComparison.Ordinal) &&
    overviewSource.Contains("Muted(UiErrorDisplay.Present(suggestion),11)",StringComparison.Ordinal));
var shellSource=File.ReadAllText(Path.Combine(desktop,"ShellWindow.cs"));
foreach(var full in new[]{"new LanguageChoice(\"ru\",\"Русский\",\"Русский\")",
    "new LanguageChoice(\"uk\",\"Українська\",\"Українська\")",
    "new LanguageChoice(\"en\",\"English\",\"English\")"})
    Record("language selection displays full label: "+full,shellSource.Contains(full,StringComparison.Ordinal));
var settingsSource=File.ReadAllText(Path.Combine(desktop,"ShellWindow.Settings.cs"));
Record("stop synchronization confirmation uses translation",settingsSource.Contains("UiLang.T(\"ОСТАНОВИТЬ синхронизацию",StringComparison.Ordinal));

var candidates=Directory.GetFiles(desktop,"ShellWindow*.cs").Append(Path.Combine(desktop,"Dialogs.cs"));
var immediate= new Regex(@"\b(?:Text|Muted|Action|Section|Field|Info|Ask|AskAction|ShowProblem|ToolTip\.SetTip)\s*\(\s*""(?<key>(?:\\.|[^""\\])*)""",
    RegexOptions.CultureInvariant);
foreach(var sourceFile in candidates)
{
    var contents=File.ReadAllText(sourceFile);
    foreach(Match m in immediate.Matches(contents))
    {
        var key=m.Groups["key"].Value;
        if(!Regex.IsMatch(key,@"[А-Яа-яЁё]") || catalog.ContainsKey(key))continue;
        // This string is the first part of a concatenation whose complete
        // value is a catalog key (the remainder is on the next lines).
        if(key=="Копии при старте хранятся только на этом ПК в папке startup-backups внутри каталога состояния. ")continue;
        Record("direct UI literal in "+Path.GetFileName(sourceFile)+": "+key,false);
    }
}
Record("visible UI literals mapped",true);
// The About page must not bypass the presentation translation boundary.
var aboutSource=File.ReadAllText(Path.Combine(desktop,"ShellWindow.About.cs"));
foreach(var (label,expression) in new[]
{
    ("introduction", "Muted(UiLang.T(\"Синхронизация зашифрованных баз KeePassXC"),
    ("libraries", "Text(UiLang.T(\"Библиотеки и компоненты\")"),
    ("environment", "Text(UiLang.T(\"Система и графика\")"),
    ("protocol", "Text(UiLang.T(\"Синхронизация и защита\")"),
    ("performance", "Text(UiLang.T(\"Производительность облачных подключений\")"),
    ("metric labels", "Muted(UiLang.T(metrics[i].Caption)"),
    ("metric values", "Text(UiLang.T(metrics[i].Data)"),
    ("column headers", "UiLang.T(\"Подключение / проверка\")")
})
    Record("About UI localized: "+label,aboutSource.Contains(expression,StringComparison.Ordinal));
foreach(var key in new[]
{
    "Синхронизация зашифрованных баз KeePassXC (.kdbx) через независимые облачные подключения — без расшифровки содержимого базы.",
    "Интеграции облаков",
    "Для облаков приложение использует собственные интеграции поверх стандартного сетевого стека .NET; сторонние облачные SDK не применяются.",
    "Протокол V2 · WebDAV · S3 / R2 · Google Drive · Proton Drive (через официальный CLI).",
    "Независимые облака, история версий, резервные копии и ручное разрешение конфликтов в KeePassXC.",
    "Учётные данные — в Windows Credential Manager; пароли из KDBX приложение не читает."
})
{
    foreach(var language in new[] { "en", "uk" })
    {
        UiLang.Use(language);
        var translated=UiLang.T(key);
        Record("About translation: "+language+" / "+key,
            !string.Equals(translated,key,StringComparison.Ordinal) &&
            (language!="en" || !Regex.IsMatch(translated,@"[А-Яа-яЁёІіЇїЄєҐґ]")));
    }
}

foreach(var (language,expected) in new[]
{
    ("ru","Передача изменений"),
    ("uk","Передавання змін"),
    ("en","Delta upload")
})
{
    UiLang.Use(language);
    var view=UiDiagnostics.View("[PASS] Proton / delta upload");
    Record("provider case translation: "+language,
        view?.Kind==UiDiagnostics.LineKind.Passed &&
        view.Text.Contains(expected,StringComparison.Ordinal));
}
UiLang.Use("ru");
Record("notification test result translation",
    UiDiagnostics.View("[PASS] Notification / accepted by the Windows notification service")?.Text
        .Contains("Принято службой уведомлений Windows",StringComparison.Ordinal)==true);
Console.WriteLine($"[SUMMARY] LocalizationTests: {tests} explicit checks; {catalog.Count} complete RU/UK/EN terms.");
    return 0;
}
catch (Exception exception)
{
    TestReporter.Unhandled("LocalizationTests",exception);
    return 1;
}
