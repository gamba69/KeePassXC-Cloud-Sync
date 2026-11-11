using System.Text.RegularExpressions;

namespace KeyPassXCCloudSync;

// Display-only presentation adapter. NEVER rewrite engine logs, report files,
// exception objects, version identifiers, remote paths, or the stored test protocol.
// The UI retains raw events and renders them in the selected language on demand.
internal static class UiDiagnostics
{
    private static string L(string ru,string uk,string en) => UiLang.Active switch
    {
        "uk" => uk, "en" => en, _ => ru
    };

    private static readonly Dictionary<string,(string Ru,string Uk,string En)> CaseNames = new(StringComparer.Ordinal)
    {
        ["Bootstrap"]=("Первоначальная настройка","Початкове налаштування","Initial setup"),
        ["Propagation"]=("Передача изменений","Передавання змін","Change propagation"),
        ["Idle poll"]=("Проверка без изменений","Перевірка без змін","Unchanged database"),
        ["Replica isolation"]=("Независимость облаков","Незалежність хмар","Cloud isolation"),
        ["Replica recovery"]=("Восстановление облака","Відновлення хмари","Cloud recovery"),
        ["External save"]=("Внешнее сохранение","Зовнішнє збереження","External save"),
        ["Invalid KDBX"]=("Повреждённая KDBX","Пошкоджена KDBX","Invalid KDBX"),
        ["Repair after corruption"]=("Восстановление после повреждения","Відновлення після пошкодження","Recovery after corruption"),
        ["Divergence"]=("Независимые изменения","Незалежні зміни","Independent changes"),
        ["Conflict retention"]=("Сохранение конфликтных версий","Збереження конфліктних версій","Conflict preservation"),
        ["CAS create"]=("Защита от одновременного создания","Захист від одночасного створення","Concurrent create protection"),
        ["CAS replace"]=("Защита от конкурентной замены","Захист від конкурентної заміни","Concurrent replacement protection"),
        ["Second computer"]=("Подключение второго устройства","Підключення другого пристрою","Second device setup"),
        ["Device identity"]=("Идентификаторы устройств","Ідентифікатори пристроїв","Device identity"),
        ["Device revocation"]=("Отключение устройства","Відкликання пристрою","Device revocation"),
        ["Device rejoin"]=("Повторное подключение устройства","Повторне підключення пристрою","Device rejoin"),
        ["Concurrent edits"]=("Одновременные изменения","Одночасні зміни","Concurrent edits"),
        ["Merge workspace"]=("Подготовка объединения","Підготовка об’єднання","Merge preparation"),
        ["Short synthetic KDBX fixture"]=("Тестовая KDBX","Тестова KDBX","Synthetic KDBX"),
        ["Backup before history"]=("Резервирование перед историей","Резервування перед історією","Backup before history"),
        ["Resume after backup"]=("Продолжение после резервирования","Продовження після резервування","Resume after backup"),
        ["Tampered immutable backup"]=("Контроль повреждённой копии","Контроль пошкодженої копії","Tampered backup rejection"),
        ["History before working KDBX"]=("История перед рабочей KDBX","Історія перед робочою KDBX","History before active KDBX"),
        ["Crash after working KDBX"]=("Сбой после замены KDBX","Збій після заміни KDBX","Crash after KDBX replacement"),
        ["Two non-CAS writers"]=("Два устройства без CAS","Два пристрої без CAS","Two writers without CAS"),
        ["Recover both conflicting heads"]=("Восстановление обеих ветвей","Відновлення обох гілок","Recover competing branches"),
        ["Pull: crash after local replace"]=("Получение: восстановление после замены KDBX","Отримання: відновлення після заміни KDBX","Pull: recover after KDBX replacement"),
        ["Pull: crash before local replace"]=("Получение: сбой до замены KDBX","Отримання: збій до заміни KDBX","Pull: crash before KDBX replacement"),
        ["Backup: corrupted local cache"]=("Восстановление повреждённой локальной копии","Відновлення пошкодженої локальної копії","Recover corrupt local backup"),
        ["Merge: changed local file rejected"]=("Merge: отказ при изменённой локальной KDBX","Merge: відмова після зміни локальної KDBX","Merge: reject changed local KDBX"),
        ["Merge: crash after local replace"]=("Merge: восстановление после аварийной замены","Merge: відновлення після аварійної заміни","Merge: recover after interrupted replace"),
        ["Merge: identical SHA crash"]=("Merge: совпадающий SHA-256 при аварии","Merge: однаковий SHA-256 під час збою","Merge: identical SHA-256 after crash"),
        ["Bootstrap A/B"]=("Подключение устройств A и B","Підключення пристроїв A і B","Devices A and B setup"),
        ["Interrupted backup and restart"]=("Сбой резервирования и восстановление","Збій резервування та відновлення","Interrupted backup recovery"),
        ["Interrupted history and restart"]=("Сбой записи истории и восстановление","Збій запису історії та відновлення","Interrupted history recovery"),
        ["Concurrent two-writer commit"]=("Конкурентная запись двух устройств","Конкурентний запис двох пристроїв","Two-device concurrent commit"),
        ["Conflict recovery and Merge workspace"]=("Восстановление конфликта и Merge","Відновлення конфлікту й Merge","Conflict and merge recovery"),
    };

    private static string CaseName(string raw)
    {
        // Some PASS lines append explanatory English text after a colon.
        var trimmed=raw.Trim();
        // Test IDs can contain a colon (Pull:, Backup:, Merge:). Match the
        // complete identifier first; the former Split(':') broke localization.
        if(CaseNames.TryGetValue(trimmed,out var names))
            return L(names.Ru,names.Uk,names.En);
        foreach(var kv in CaseNames.OrderByDescending(k=>k.Key.Length))
            if(trimmed.StartsWith(kv.Key+": ",StringComparison.Ordinal))
                return L(kv.Value.Ru,kv.Value.Uk,kv.Value.En);
        // Live provider tests use stable English identifiers in logs and reports;
        // only the display text is localized.
        var separator=trimmed.IndexOf(" / ",StringComparison.Ordinal);
        if(separator>0)
        {
            var subject=trimmed[..separator];
            var displayedSubject=subject switch
            {
                "Notification"=>L("Уведомление","Сповіщення","Notification"),
                "Cloud diagnostics"=>L("Диагностика облака","Діагностика хмари","Cloud diagnostics"),
                _=>subject
            };
            var suffix=trimmed[(separator+3)..];
            (string Id,string Ru,string Uk,string En)[] liveCases=
            [
                ("initial cloud bootstrap","Первоначальная загрузка","Початкове завантаження","Initial cloud bootstrap"),
                ("idle polling without content upload","Опрос без передачи файлов","Опитування без передавання файлів","Idle poll without content upload"),
                ("second device bootstrap","Подключение второго устройства","Підключення другого пристрою","Second device bootstrap"),
                ("independent concurrent edits and conflict retention","Конкурентные изменения и сохранение конфликта","Конкурентні зміни та збереження конфлікту","Concurrent edits and conflict retention"),
                ("delta upload","Передача изменений","Передавання змін","Delta upload"),
                ("delta download and KDBX equality","Получение и сверка KDBX","Отримання та перевірка KDBX","Delta download and KDBX equality"),
                ("isolated test directory cleanup","Очистка тестовой папки","Очищення тестової папки","Test directory cleanup"),
                ("accepted by the Windows notification service","Принято службой уведомлений Windows","Прийнято службою сповіщень Windows","Accepted by Windows notification service"),
                ("service rejected the test","Ошибка отправки тестового уведомления","Помилка надсилання тестового сповіщення","Test notification rejected"),
                ("saved cloud connection required","Нужно сохранённое подключение к облаку","Потрібне збережене підключення до хмари","A saved cloud connection is required")
            ];
            foreach(var (id,ru,uk,en) in liveCases)
                if(suffix==id || suffix.StartsWith(id+":",StringComparison.Ordinal))
                    return displayedSubject+" / "+L(ru,uk,en)+suffix[id.Length..];
        }
        if(trimmed.StartsWith("Cloud race ",StringComparison.OrdinalIgnoreCase))
            return L("Конкурентная запись: ","Конкурентний запис: ","Concurrent writes: ")+trimmed[11..];
        if(trimmed.StartsWith("Cloud ",StringComparison.OrdinalIgnoreCase))
            return L("Проверка облака: ","Перевірка хмари: ","Cloud check: ")+trimmed[6..];
        return trimmed; // preserves unknown identifiers; caller displays failures verbatim
    }

    // The human-friendly UI never guesses the meaning of an unknown test ID.
    // Descriptions complement the short name without repeating the English
    // source protocol. Technical reports still include the original descriptions.
    private static readonly Dictionary<string,(string Ru,string Uk,string En)> CaseDescriptions = new(StringComparer.Ordinal)
    {
        ["Bootstrap"] = ("Создание идентификаторов базы и первых версий в двух облаках.", "Створення ідентифікаторів бази й початкових версій у двох хмарах.", "Creates vault IDs and initial versions in two clouds."),
        ["Propagation"] = ("Локальное изменение создаёт новую версию в обоих облаках.", "Локальна зміна створює нову версію в обох хмарах.", "A local edit propagates a new version to both clouds."),
        ["Idle poll"] = ("Без изменения KDBX история и контрольные маркеры не переписываются.", "Без змін KDBX історія й контрольні маркери не переписуються.", "No revision or marker rewrite when the KDBX is unchanged."),
        ["Replica isolation"] = ("Сбой одного облака не блокирует публикацию в другое.", "Збій однієї хмари не блокує публікацію в іншу.", "One unsafe cloud cannot block the healthy replica."),
        ["Replica recovery"] = ("Восстановленное облако догоняет исправное без повреждений.", "Відновлена хмара наздоганяє справну без пошкоджень.", "A recovered cloud catches up without altering the healthy replica."),
        ["External save"] = ("Сохранение KeePassium без метаданных принимается при отсутствии конфликта.", "Збереження KeePassium без метаданих приймається без конфлікту.", "Accepts a KeePassium save without metadata if no conflict exists."),
        ["Invalid KDBX"] = ("Повреждённая облачная KDBX изолируется; другая реплика доступна.", "Пошкоджена хмарна KDBX ізолюється; інша репліка доступна.", "Quarantines a malformed remote KDBX; healthy replica stays usable."),
        ["Repair after corruption"] = ("После восстановления исходной KDBX синхронизация возобновляется.", "Після відновлення вихідної KDBX синхронізація відновлюється.", "Restored KDBX resumes synchronization."),
        ["Divergence"] = ("Несогласованные изменения компьютера и телефона дают конфликт версий.", "Несумісні зміни комп’ютера й телефона утворюють конфлікт версій.", "Independent desktop and phone saves trigger a persistent conflict."),
        ["Conflict retention"] = ("Обе конфликтующие KDBX сохраняются без потерь.", "Обидві конфліктні KDBX зберігаються без втрат.", "Both conflicting KDBX files remain protected."),
        ["CAS create"] = ("Устаревшее условие не позволяет второму писателю создать тот же файл.", "Застаріла умова не дозволяє другому записувачу створити той самий файл.", "Stale conditional creation cannot overwrite competing creation."),
        ["CAS replace"] = ("Устаревшая ревизия не может затереть свежую запись другого устройства.", "Застаріла ревізія не може затерти новий запис іншого пристрою.", "Stale revision cannot overwrite another writer's new revision."),
        ["Second computer"] = ("Второе устройство получает базу из облачных копий первого.", "Другий пристрій отримує базу з хмарних копій першого.", "Second installation bootstraps from first device's replicas."),
        ["Device identity"] = ("Общий идентификатор базы и уникальные идентификаторы устройств.", "Спільний ідентифікатор бази й унікальні ідентифікатори пристроїв.", "Same vault ID, distinct device IDs."),
        ["Device revocation"] = ("Отключённое устройство больше не вправе публиковать изменения.", "Відкликаний пристрій більше не може публікувати зміни.", "A revoked device cannot publish new writes."),
        ["Device rejoin"] = ("Повторное подключение создаёт новую личность устройства и синхронизирует облака.", "Повторне підключення створює новий ідентифікатор пристрою й синхронізує хмари.", "Rejoined device gets a new identity and synchronizes replicas."),
        ["Concurrent edits"] = ("Разные неопубликованные изменения двух устройств защищаются конфликтом.", "Різні неопубліковані зміни двох пристроїв захищаються конфліктом.", "Two unpublished edits create a protected conflict."),
        ["Merge workspace"] = ("Рабочая копия для ручного объединения создаётся отдельно от оригиналов.", "Робоча копія для ручного об’єднання створюється окремо від оригіналів.", "Merge workspace is separate from original KDBX versions."),
        ["Short synthetic KDBX fixture"] = ("Короткое имя теста всё равно формирует корректный заголовок KDBX.", "Коротке ім’я тесту все одно формує коректний заголовок KDBX.", "Short labels still produce valid synthetic KDBX headers."),
        ["Backup before history"] = ("Новая история не публикуется, пока резервная копия не подтверждена.", "Нова історія не публікується до підтвердження резервної копії.", "History never appears before an immutable backup is safe."),
        ["Resume after backup"] = ("После сбоя публикация продолжается с прежним ID версии.", "Після збою публікація продовжується з попереднім ID версії.", "Interrupted publication resumes with the same version ID."),
        ["Tampered immutable backup"] = ("Изменённая архивная KDBX отвергается, исправная реплика сохраняется.", "Змінена архівна KDBX відхиляється, справна репліка зберігається.", "Tampered immutable backup is rejected; healthy cloud survives."),
        ["History before working KDBX"] = ("Сбой после истории, но до рабочей KDBX безопасно восстанавливается.", "Збій після історії, але до робочої KDBX безпечно відновлюється.", "Recovery after history and before working-file replacement."),
        ["Crash after working KDBX"] = ("После замены KDBX восстанавливается SHA-маркер без лишней версии.", "Після заміни KDBX відновлюється SHA-маркер без зайвої версії.", "Restores SHA marker after replacement without a new revision."),
        ["Two non-CAS writers"] = ("Обе версии двух одновременно пишущих устройств сохраняются без CAS.", "Обидві версії двох одночасних записувачів зберігаються без CAS.", "Both concurrent versions survive without server CAS."),
        ["Recover both conflicting heads"] = ("После гонки система обнаруживает обе ветви и сохраняет точные KDBX.", "Після гонки система виявляє обидві гілки й зберігає точні KDBX.", "Finds both DAG heads and archives exact conflicting KDBX bytes."),
        ["Pull: crash after local replace"] = ("После аварийной локальной замены не возникает ложной второй ветки.", "Після аварійної локальної заміни не виникає хибної другої гілки.", "A crash after local install does not invent a sibling DAG head."),
        ["Pull: crash before local replace"] = ("Авария до замены KDBX безопасно повторяет установку.", "Аварія до заміни KDBX безпечно повторює встановлення.", "A crash before replacing local KDBX can replay safely."),
        ["Backup: corrupted local cache"] = ("Плохая локальная копия изолируется и восстанавливается по облачному SHA.", "Погана локальна копія ізолюється й відновлюється за хмарним SHA.", "Corrupt local cache is quarantined and restored from verified cloud bytes."),
        ["Merge: changed local file rejected"] = ("Устаревшее объединение не публикует новую вершину DAG.", "Застаріле об’єднання не публікує нову вершину DAG.", "A stale merge cannot publish a new DAG head."),
        ["Merge: crash after local replace"] = ("После сбоя подтверждённый Merge публикует ровно одну вершину.", "Після збою підтверджений Merge публікує рівно одну вершину.", "A staged merge recovers with exactly one DAG head."),
        ["Merge: identical SHA crash"] = ("Merge с теми же байтами KDBX всё равно объединяет обе ветви.", "Merge з тими самими байтами KDBX усе одно об’єднує обидві гілки.", "An unchanged-KDBX merge still closes both DAG heads."),
        ["Bootstrap A/B"] = ("Два устройства получают независимые состояния и общую облачную базу.", "Два пристрої отримують незалежні стани й спільну хмарну базу.", "Two installations share one cloud vault with separate local states."),
        ["Interrupted backup and restart"] = ("Сбой при записи резерва корректно восстанавливается после перезапуска.", "Збій під час запису резерву коректно відновлюється після перезапуску.", "Interrupted cloud backup is recovered after restart."),
        ["Interrupted history and restart"] = ("Сбой при публикации истории корректно восстанавливается.", "Збій під час публікації історії коректно відновлюється.", "Interrupted history publication recovers after restart."),
        ["Concurrent two-writer commit"] = ("Оба конкурентных коммита сохраняют immutable KDBX, SHA-256 и DAG.", "Обидва конкурентні коміти зберігають immutable KDBX, SHA-256 і DAG.", "Both concurrent commits preserve immutable KDBX, SHA-256 and DAG."),
        ["Conflict recovery and Merge workspace"] = ("Обе ветви доступны для ручного объединения в отдельном каталоге.", "Обидві гілки доступні для ручного об’єднання в окремому каталозі.", "Both conflicting branches remain available for manual merge."),
    };

    internal enum LineKind { Info, Section, Passed, Failed, Skipped, Summary, Waiting, Cleanup, Notice, Network }
    internal sealed record DisplayLine(LineKind Kind,string Text,string? Hint=null,
        int Passed=0,int Failed=0,int Skipped=0);

    internal static bool TrySummary(string raw,out int passed,out int failed,out int skipped)
    {
        passed=0;failed=0;skipped=0;
        var match=Regex.Match(raw,@"^Summary: (?<p>\d+) PASS, (?<f>\d+) FAIL(?:, (?<s>\d+) SKIP)? \(\d+ (?:total|clouds)\)$");
        return match.Success && int.TryParse(match.Groups["p"].Value,out passed) &&
            int.TryParse(match.Groups["f"].Value,out failed) &&
            (!match.Groups["s"].Success || int.TryParse(match.Groups["s"].Value,out skipped));
    }
    internal static string SummaryText(int passed,int failed,int skipped) =>
        L("Итог: ","Підсумок: ","Result: ")+passed+L(" успешно · "," успішно · "," passed · ")+
        failed+L(" ошибок · "," помилок · "," failed · ")+skipped+
        L(" пропущено"," пропущено"," skipped");
    internal static (string Prefix,string Pass,string Fail,string Skip) SummaryLabels() => UiLang.Active switch
    {
        "uk" => ("Підсумок: "," успішно"," помилок"," пропущено"),
        "en" => ("Result: "," passed"," failed"," skipped"),
        _ => ("Итог: "," успешно"," ошибок"," пропущено")
    };
    internal static DisplayLine? View(string raw)
    {
        var text=Line(raw);
        if(text is null)return null;
        if(TrySummary(raw,out var passed,out var failed,out var skipped))
            return new(LineKind.Summary,text,null,passed,failed,skipped);
        foreach(var (marker,kind) in new[]
        {
            ("[PASS] ",LineKind.Passed),
            ("[FAIL] ",LineKind.Failed),("[SKIP] ",LineKind.Skipped)
        })
        {
            if(!raw.StartsWith(marker,StringComparison.Ordinal))continue;
            var content=raw[marker.Length..].Trim();
            var name=content;
            var hint=CaseDescriptions.TryGetValue(name,out var detail) ? L(detail.Ru,detail.Uk,detail.En):null;
            // Strip the inline glyph only: the separate glyph column owns its color.
            if(kind==LineKind.Passed && text.StartsWith("✓ ",StringComparison.Ordinal))text=text[2..];
            return new(kind,text,hint);
        }
        if(raw.StartsWith("KeePassSync tests:",StringComparison.Ordinal))return new(LineKind.Section,text);
        if(raw.StartsWith("[WAIT]",StringComparison.Ordinal))return new(LineKind.Waiting,text);
        // New logs pad NET to the same width as PASS and FAIL. Old reports remain readable.
        if(raw.StartsWith("[NET ] ",StringComparison.Ordinal) ||
           raw.StartsWith("[NET] ",StringComparison.Ordinal))return new(LineKind.Network,text);
        if(raw.StartsWith("[CLEANUP]",StringComparison.Ordinal))return new(LineKind.Cleanup,text);
        if(raw.StartsWith("[NOTIFY]",StringComparison.Ordinal))return new(LineKind.Notice,text);
        if(raw.StartsWith("Failure details:",StringComparison.Ordinal) || raw.StartsWith("[FAIL]",StringComparison.Ordinal))
            return new(LineKind.Failed,text);
        return new(LineKind.Info,text);
    }

    // Blank => duplicate technical lines suppressed in the UI, not in source reports.
    internal static string? Line(string raw)
    {
        if(string.IsNullOrWhiteSpace(raw))return null;
        if(raw.StartsWith("       ",StringComparison.Ordinal))
        {
            var detail=raw.Trim();
            if(detail.StartsWith("Reason: ",StringComparison.Ordinal))
                return L("Причина: ","Причина: ","Reason: ")+UiErrorDisplay.Present(detail[8..]);
            if(detail.StartsWith("Test-only folder: ",StringComparison.Ordinal))
                return L("Тестовая папка: ","Тестова тека: ","Test folder: ")+detail[18..];
            if(detail.StartsWith("Локальные тестовые данные сохранены: ",StringComparison.Ordinal))
                return L("Тестовые данные сохранены: ","Тестові дані збережено: ","Test data kept: ")+detail[("Локальные тестовые данные сохранены: ".Length)..];
            return null; // test descriptions duplicate each start and completion
        }
        if(raw.StartsWith("KeePassSync tests: ",StringComparison.Ordinal))
        {
            var suite=raw[19..];
            var title=suite switch
            {
                "local synchronization"=>L("Локальная синхронизация","Локальна синхронізація","Local synchronization"),
                "safety and concurrency"=>L("Защита данных и конкуренция","Захист даних і конкуренція","Safety and concurrency"),
                "immutable publication and non-CAS concurrency"=>L("Публикация версий и запись без CAS","Публікація версій і запис без CAS","Version publication and non-CAS writes"),
                "local crash/merge/backup regressions"=>L("Восстановление и Merge","Відновлення та Merge","Recovery and merge"),
                "real-cloud two-installation concurrency"=>L("Облачная конкуренция двух устройств","Хмарна конкуренція двох пристроїв","Two-device cloud concurrency"),
                _=>suite
            };
            return L("Раздел: ","Розділ: ","Suite: ")+title;
        }
        string[] statuses=["[PASS] ","[FAIL] ","[SKIP] "];
        foreach(var prefix in statuses)
        {
            if(!raw.StartsWith(prefix,StringComparison.Ordinal))continue;
            var content=raw[prefix.Length..].Trim();
            // The English raw result survives in reports; UI uses localized case name.
            var stage=prefix switch
            {
                "[PASS] "=>L("✓ Пройдено: ","✓ Пройдено: ","✓ Passed: "),
                "[SKIP] "=>L("Пропущено: ","Пропущено: ","Skipped: "),
                _=>L("ОШИБКА: ","ПОМИЛКА: ","FAILED: ")
            };
            if(content.StartsWith("Cloud race: выбранное облако не найдено",StringComparison.Ordinal))
                content=L("Облако не найдено","Хмару не знайдено","Cloud not found");
            // OS-originated exceptions are kept in raw reports, but the visible
            // diagnostics row must use the selected application language.
            if(Regex.IsMatch(content,@"\b[A-Za-z][A-Za-z0-9]*Exception\s*:",RegexOptions.CultureInvariant))
                return stage+UiErrorDisplay.Present(content);
            return stage+CaseName(content);
        }
        if(TrySummary(raw,out var passed,out var failed,out var skipped))
            return SummaryText(passed,failed,skipped);
        if(raw.StartsWith("Reports: ",StringComparison.Ordinal))
            return L("Отчёты: ","Звіти: ","Reports: ")+raw[9..];
        if(raw.StartsWith("[STEP] ",StringComparison.Ordinal))
            return L("Этап: ","Етап: ","Step: ")+Progress(raw[7..]);
        if(raw.StartsWith("[NET ] ",StringComparison.Ordinal) ||
           raw.StartsWith("[NET] ",StringComparison.Ordinal))
        {
            var detail=raw[(raw.StartsWith("[NET ] ",StringComparison.Ordinal)?7:6)..];
            // Routine HTTP 200 replies are not useful in the human-facing view.
            // Every error and slow call remains visible.
            if(detail.Contains("HTTP 200",StringComparison.Ordinal) && !detail.Contains("МЕДЛЕННО",StringComparison.Ordinal))return null;
            if(detail.Contains(" — отправка",StringComparison.Ordinal) || detail.Contains(" — подготовка / OAuth",StringComparison.Ordinal))return null;
            return L("Сеть: ","Мережа: ","Network: ")+Network(detail);
        }
        if(raw.StartsWith("[CLEANUP] ",StringComparison.Ordinal))
            return L("Очистка: ","Очищення: ","Cleanup: ")+Progress(raw[10..]);
        if(raw.StartsWith("[WAIT] ",StringComparison.Ordinal))
        {
            var wait=raw[7..];
            var reason=Regex.Match(wait,@"^нет нового вывода от операции; последний этап: (.+) · прошло (\d\d:\d\d(?::\d\d)?), без сообщений теста \d+ с$");
            if(reason.Success)
                return L("Ожидание ответа · ","Очікування відповіді · ","Waiting for response · ")+
                    Progress(reason.Groups[1].Value)+" · "+reason.Groups[2].Value;
            return L("Ожидание: ","Очікування: ","Waiting: ")+Progress(wait);
        }
        if(raw.StartsWith("[CANCEL] ",StringComparison.Ordinal))
            return L("Остановка: ","Зупинка: ","Stopping: ")+Progress(raw[9..]);
        if(raw.StartsWith("[NOTIFY] ",StringComparison.Ordinal))
        {
            // This is a stable event protocol, not already-localized prose.
            // A new render after changing language translates every prior event.
            var evt=raw[9..];
            var detail=evt switch
            {
                "prepare" => Progress("Подготовка системного уведомления…"),
                "sent" => Progress("Системное уведомление передано Windows; показ зависит от настроек уведомлений."),
                _ when evt.StartsWith("failed|",StringComparison.Ordinal) =>
                    Progress("Не удалось отправить системное уведомление:")+" "+
                    evt[7..].Replace('|',' '),
                _ => Progress(evt)
            };
            return L("Уведомление: ","Сповіщення: ","Notification: ")+detail;
        }
        if(raw.StartsWith("[INFO] ",StringComparison.Ordinal))
        {
            var info=raw[7..];
            var finished=Regex.Match(info,@"^Завершено за (.+)\. Итог: (.+)$");
            if(finished.Success)
                return L("Завершено за ","Завершено за ","Completed in ")+finished.Groups[1].Value+
                    L(" · итог: "," · підсумок: "," · result: ")+Progress(finished.Groups[2].Value);
            return Progress(info);
        }
        if(raw.StartsWith("Note: ",StringComparison.Ordinal))
        {
            if(raw.Contains("synthetic file bytes",StringComparison.Ordinal))
                return L("Тестовые KDBX: реальные базы и облачные API не проверялись.",
                    "Тестові KDBX: реальні бази й хмарні API не перевірялися.",
                    "Synthetic KDBX: real vaults and cloud APIs were not tested.");
            if(raw.Contains("CAS",StringComparison.Ordinal))
                return L("CAS облака проверяется только при серверной поддержке.","CAS хмари перевіряється лише за підтримки сервера.","Cloud CAS is verified only when supported by the server.");
        }
        if(raw.StartsWith("Failure details: ",StringComparison.Ordinal))
            return L("Подробности ошибки: ","Подробиці помилки: ","Failure details: ")+raw[17..];
        if(raw.StartsWith("Test files retained: ",StringComparison.Ordinal) || raw.StartsWith("Safety test files retained: ",StringComparison.Ordinal))
            return L("Тестовые файлы сохранены: ","Тестові файли збережено: ","Test files retained: ")+raw[(raw.IndexOf(':')+2)..];
        // Never suppress an unexpected failure or an unrecognised diagnostic message.
        return Progress(raw);
    }

    internal static string Network(string raw)
    {
        // Prefer brief localized description. HTTP code, latency and provider identity
        // remain intact so a real failure can be diagnosed from the original logs.
        var status=Regex.Match(raw,@"HTTP \d{3}");
        if(status.Success)
        {
            var owner=raw.Split(':',2)[0];
            var latency=Regex.Match(raw,@"\d+(?:[.,]\d+)?\s*(?:мс|ms|с|s)\b");
            return owner+" · "+status.Value+
                (latency.Success?" · "+latency.Value:"")+
                (raw.Contains("МЕДЛЕННО",StringComparison.Ordinal)
                    ?L(" · медленный ответ"," · повільна відповідь"," · slow response"):"");
        }
        if(Regex.IsMatch(raw,@"\b[A-Za-z][A-Za-z0-9]*Exception\s*:",RegexOptions.CultureInvariant))
            return UiErrorDisplay.Present(raw);
        // Unknown errors are never discarded from the technical report.
        return raw.Replace(" — МЕДЛЕННО",L(" · медленно"," · повільно"," · slow"),StringComparison.Ordinal);
    }

    private static readonly Dictionary<string,(string Uk,string En)> ProgressTerms = new(StringComparer.Ordinal)
    {
        ["Читаю локальное состояние и историю версий…"]=("Читаю локальний стан та історію версій…","Reading local state and version history…"),
        ["Сверяю историю версий и контрольные точки…"]=("Звіряю історію версій і контрольні точки…","Checking version history and checkpoints…"),
        ["Проверяю локальную KDBX и SHA256…"]=("Перевіряю локальну KDBX і SHA256…","Verifying local KDBX and SHA256…"),
        ["Проверяю внешние изменения и происхождение версий…"]=("Перевіряю зовнішні зміни та походження версій…","Checking external changes and version origins…"),
        ["Определяю актуальную ветку и возможные конфликты…"]=("Визначаю актуальну гілку та можливі конфлікти…","Checking current branch and conflicts…"),
        ["Проверяю контрольные точки и резервные копии…"]=("Перевіряю контрольні точки й резервні копії…","Checking checkpoints and backups…"),
        ["Сверяю параллельные публикации после записи…"]=("Звіряю паралельні публікації після запису…","Verifying concurrent publications…"),
        ["Читаю идентификатор базы…"]=("Читаю ідентифікатор бази…","Reading database identity…"),
        ["Сверяю историю версий…"]=("Звіряю історію версій…","Checking version history…"),
        ["Проверяю контрольные точки…"]=("Перевіряю контрольні точки…","Checking checkpoints…"),
        ["Проверяю исключённые устройства…"]=("Перевіряю відкликані пристрої…","Checking revoked devices…"),
        ["Проверяю зарегистрированные устройства…"]=("Перевіряю зареєстровані пристрої…","Checking registered devices…"),
        ["Проверяю рабочую KDBX и SHA256…"]=("Перевіряю робочу KDBX і SHA256…","Verifying active KDBX and SHA256…"),
        ["Сверяю маркер целостности…"]=("Звіряю маркер цілісності…","Checking integrity marker…"),
        ["Проверяю каталоги и служебные метаданные…"]=("Перевіряю каталоги й службові метадані…","Checking directories and metadata…"),
        ["Публикую и проверяю неизменяемые копии…"]=("Публікую й перевіряю незмінні копії…","Publishing and verifying immutable copies…"),
        ["Регистрирую подтверждённые версии…"]=("Реєструю підтверджені версії…","Registering verified versions…"),
        ["Проверяю маркер и подтверждение устройства…"]=("Перевіряю маркер та підтвердження пристрою…","Checking marker and device acknowledgment…"),
        ["Сверяю содержимое и резервные копии: "]=("Звіряю вміст і резервні копії: ","Checking contents and backups: "),
        ["KDBX на паузе · топология облаков опрашивается"]=("KDBX на паузі · опитування топології хмар","KDBX paused · polling cloud topology"),
        ["KDBX на паузе · проверяю топологию облаков…"]=("KDBX на паузі · перевіряю топологію хмар…","KDBX paused · checking cloud topology…"),
        ["KDBX на паузе · следующий опрос топологии"]=("KDBX на паузі · наступне опитування топології","KDBX paused · next topology poll"),
        ["Сверяю маркер целостности..."]=("Звіряю маркер цілісності...","Checking integrity marker..."),
        ["Настройки изменены · проверяю доступность реплик…"]=("Налаштування змінено · перевіряю доступність реплік…","Settings changed · checking cloud replicas…"),
        ["Выберите базу KDBX и настройте облака"]=("Виберіть базу KDBX і налаштуйте хмари","Select a KDBX database and configure clouds"),
        ["Проверяю локальную базу и состояние облаков…"]=("Перевіряю локальну базу і стан хмар…","Checking local database and cloud status…"),
        ["Ожидаю следующей проверки"]=("Очікую наступної перевірки","Waiting for next check"),
        ["Ожидаю фонового обслуживания"]=("Очікую фонового обслуговування","Waiting for background maintenance"),
        ["Обнаружено событие · жду стабилизации файла"]=("Виявлено подію · чекаю стабілізації файлу","File changed · waiting for stabilization"),
        ["Ошибка фоновой проверки · повторная попытка"]=("Помилка фонової перевірки · повторна спроба","Background check failed · retry scheduled"),
        ["Ожидаю настройки"]=("Очікую налаштування","Waiting for setup"),
        ["Обнаружены изменения локальной KDBX"]=("Виявлено зміни локальної KDBX","Local KDBX changes detected"),
        ["Первичная публикация KDBX"]=("Початкова публікація KDBX","Initial KDBX publication"),
        ["Восстановление локальной KDBX из реплики"]=("Відновлення локальної KDBX з репліки","Restoring local KDBX from replica"),
        ["Внешнее изменение KDBX: "]=("Зовнішня зміна KDBX: ","External KDBX change: "),
        ["Тестовый каталог удалён"]=("Тестовий каталог видалено","Test folder deleted"),
        ["Подготовка системного уведомления…"]=("Підготовка системного сповіщення…","Preparing system notification…"),
        ["Системное уведомление передано Windows; показ зависит от настроек уведомлений."]=("Системне сповіщення передано Windows; показ залежить від налаштувань.","Notification sent to Windows; display depends on settings."),
        ["Не удалось отправить системное уведомление:"]=("Не вдалося надіслати системне сповіщення:","Could not send notification:"),
        ["Запущено: "]=("Запущено: ","Started: "),
        ["Для сетевого теста нужна сохранённая конфигурация с облаком."]=("Для мережевого тесту потрібна збережена конфігурація хмари.","A saved cloud configuration is required for network tests."),
        ["Рабочая KDBX не используется. Обращение к облаку только в отдельной временной тестовой папке."]=("Робоча KDBX не використовується. Хмарні операції виконуються лише в окремій тестовій теці.","The working KDBX is untouched. Cloud operations use an isolated test folder."),
        ["Изолированные локальные данные; подключение к облакам не требуется."]=("Ізольовані локальні дані; підключення до хмар не потрібне.","Isolated local data; cloud access is not required."),
        ["Фоновая синхронизация заняла движок; тест начнётся после её окончания."]=("Фонова синхронізація зайняла рушій; тест почнеться після її завершення.","Waiting for the background sync to release the engine."),
        ["Тестовый сценарий запущен."]=("Тестовий сценарій запущено.","Test scenario started."),
        ["Запуск остановлен; результат не считается PASS."]=("Запуск зупинено; результат не вважається успішним.","Test stopped; it is not marked as passed."),
        ["Сценарий отменён. Результат очистки тестовой папки см. выше в журнале."]=("Сценарій скасовано. Результат очищення тестової теки див. вище.","Scenario cancelled. See cleanup result above."),
        ["Все сценарии завершены успешно."]=("Усі сценарії виконано успішно.","All scenarios passed."),
        ["Внимание: один или несколько сценариев завершились с ошибкой."]=("Увага: один або кілька сценаріїв завершилися з помилкою.","One or more scenarios failed."),
        ["Остановка запрошена. Прерываю сетевые операции…"]=("Запит на зупинку прийнято. Перериваю мережеві операції…","Stop requested. Cancelling network operations…"),
        ["Остановлено до следующего облака."]=("Зупинено до наступної хмари.","Stopped before the next cloud."),
        ["Завершаю сетевые операции; удаляю только изолированный тестовый каталог."]=("Завершую мережеві операції; видаляю лише ізольований тестовий каталог.","Finishing network operations; deleting only the isolated test folder."),
        ["Изолированный облачный каталог удалён."]=("Ізольований хмарний каталог видалено.","Isolated cloud folder deleted."),
        ["Остальные облака не проверяются: получен запрос остановки."]=("Інші хмари не перевірятимуться: отримано запит на зупинку.","Remaining clouds skipped because stop was requested."),
        ["Создаю отдельную тестовую папку"]=("Створюю окрему тестову теку","Creating isolated test folder"),
        ["Загружаю тестовый файл"]=("Завантажую тестовий файл","Uploading test file"),
        ["Проверяю чтение и SHA256"]=("Перевіряю читання й SHA256","Verifying download and SHA256"),
        ["Проверяю запрет повторного создания"]=("Перевіряю заборону повторного створення","Checking duplicate create protection"),
        ["Публикую два независимых файла параллельно"]=("Публікую два незалежних файли паралельно","Publishing two independent files concurrently"),
        ["Удаляю только изолированный тестовый каталог"]=("Видаляю лише ізольований тестовий каталог","Deleting only isolated test folder"),
        ["Устройство A: первоначальная публикация"]=("Пристрій A: початкова публікація","Device A: initial publication"),
        ["Устройство B: получение исходной версии"]=("Пристрій B: отримання початкової версії","Device B: downloading initial version"),
        ["жду освобождения движка синхронизации"]=("чекаю звільнення рушія синхронізації","waiting for synchronization engine"),
        ["остановка в процессе; возможно, выполняется очистка тестовых данных"]=("зупинка триває; можливе очищення тестових даних","stopping; test data may be cleaning up"),
        ["нет нового вывода от операции; последний этап: "]=("немає нових повідомлень; останній етап: ","no new output; last step: "),
        ["Устройство A: "]=("Пристрій A: ","Device A: "),
        ["Устройство B: "]=("Пристрій B: ","Device B: "),
        ["очистка: "]=("очищення: ","cleanup: "),
        ["Не удалось передать отмену: "]=("Не вдалося передати скасування: ","Failed to cancel: "),
        ["Не удалось завершить удаление: "]=("Не вдалося завершити видалення: ","Could not finish cleanup: "),
        ["Для ручной проверки оставлен ТОЛЬКО тестовый каталог: "]=("Для ручної перевірки залишено ЛИШЕ тестовий каталог: ","ONLY the test folder remains for manual review: "),
        ["Локальные тестовые данные сохранены: "]=("Локальні тестові дані збережено: ","Local test data retained: "),
        ["Завершено за "]=("Завершено за ","Completed in "),
        ["Итог: "]=("Підсумок: ","Result: "),
        ["Прошло "]=("Минуло ","Elapsed "),
        ["прошло "]=("минуло ","elapsed "),
        ["без сообщений теста "]=("без повідомлень тесту ","without test output "),
        [" с"]=(" с"," s"),
        ["проверяю"]=("перевіряю","checking"),
    };

    internal static string Progress(string raw)
    {
        if(string.IsNullOrEmpty(raw))return "";
        var s=raw.Trim();
        // Dynamic cloud name is deliberately not translated (user-configured).
        if(s.StartsWith("[",StringComparison.Ordinal) && s.IndexOf(']') is var i && i>1 && i<80)
            return s[..(i+1)]+" "+Progress(s[(i+1)..]);
        var remaining=Regex.Match(s,@"^(.*) · осталось (\d{2}:\d{2}(?::\d{2})?)$");
        if(remaining.Success)
            return Progress(remaining.Groups[1].Value)+L(" · осталось "," · залишилося "," · remaining ")+remaining.Groups[2].Value;
        foreach(var term in ProgressTerms.OrderByDescending(kv=>kv.Key.Length))
        {
            if(s.Equals(term.Key,StringComparison.Ordinal))return L(term.Key,term.Value.Uk,term.Value.En);
            if(s.StartsWith(term.Key,StringComparison.Ordinal) && term.Key.EndsWith(' '))
                return L(term.Key,term.Value.Uk,term.Value.En)+Progress(s[term.Key.Length..]);
        }
        // Progress emitted by Core contains user-defined cloud names. Translate
        // only the fixed label; never translate the provider/account name.
        var cloudChanges=Regex.Match(s,@"^Проверяю изменения в (.+)…$");
        if(cloudChanges.Success)
            return L("Проверяю изменения в ","Перевіряю зміни в ","Checking changes in ")+
                cloudChanges.Groups[1].Value+"…";
        var cloudCheck=Regex.Match(s,@"^Проверяю (.+) \((\d+)/(\d+)\)…$");
        if(cloudCheck.Success)return L("Проверяю ","Перевіряю ","Checking ")+cloudCheck.Groups[1].Value+" ("+cloudCheck.Groups[2].Value+"/"+cloudCheck.Groups[3].Value+")…";
        if(CaseNames.TryGetValue(s,out var caseNames))return L(caseNames.Ru,caseNames.Uk,caseNames.En);
        if(s.StartsWith("A: ",StringComparison.Ordinal) || s.StartsWith("B: ",StringComparison.Ordinal))
            return s[..3]+Progress(s[3..]);
        if(s.Contains(" · прошло ",StringComparison.Ordinal))
        {
            var bits=s.Split(" · прошло ",2,StringSplitOptions.None);
            return Progress(bits[0])+L(" · прошло "," · минуло "," · elapsed ")+Progress(bits[1]);
        }
        var ui=UiLang.T(s);
        if(!string.Equals(ui,s,StringComparison.Ordinal))return ui;
        // Display raw unexpected diagnostics rather than hiding operational failures.
        return s;
    }

    // Cloud states come from the engine as invariant protocol identifiers.
    // Do not display SYNCED / OFFLINE / ERROR literally in any UI language.
    internal static string CloudState(string? state) => state switch
    {
        "SYNCED" => L("Синхронизировано","Синхронізовано","Synchronized"),
        "SYNCING" => L("Синхронизация…","Синхронізація…","Synchronizing…"),
        "OFFLINE" => L("Недоступно","Недоступно","Offline"),
        "DEGRADED" => L("Частично доступно","Частково доступно","Partially available"),
        "CONFLICT" => L("Конфликт версий","Конфлікт версій","Version conflict"),
        "ERROR" => L("Ошибка","Помилка","Error"),
        "NeverRun" or null or "" => L("Не проверено","Не перевірено","Not checked"),
        _ => state ?? ""
    };

    internal static string Result(string raw) => Progress(raw);
    internal static string Activity(string mode,string step,TimeSpan elapsed)
        =>L("Выполняется: ","Виконується: ","Running: ")+Progress(mode)+" · "+
          L("прошло ","минуло ","elapsed ")+ (elapsed.TotalHours>=1
          ?$"{(int)elapsed.TotalHours:00}:{elapsed.Minutes:00}:{elapsed.Seconds:00}"
          :$"{elapsed.Minutes:00}:{elapsed.Seconds:00}")+" · "+Progress(step);
}
