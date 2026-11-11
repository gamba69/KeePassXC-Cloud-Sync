using Avalonia.Controls.Templates;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Controls.Documents;
using Avalonia.Layout;
using Avalonia.Input.Platform;
using Avalonia.Media;
using Avalonia.Platform.Storage;
using Avalonia.Styling;
using Avalonia.Threading;
using System.Diagnostics;
using System.Collections.Concurrent;
using System.Text;

namespace KeyPassXCCloudSync;

internal sealed partial class ShellWindow : Window
{
    private Control BuildDiagnostics()
    {
        var root=Column(13);
        root.Children.Add(Text("Диагностика",27,true));
        root.Children.Add(Muted("Проверки выполняются на тестовых данных. Рабочая KDBX не изменяется; облачные тесты создают и удаляют только собственную временную папку.",13));

        var actions=new[]
        {
            Action("Локальные тесты",()=>_=RunTestsAsync("local"),true,"beaker"),
            Action("Безопасность",()=>_=RunTestsAsync("safety"),icon:"shield"),
            Action("Облачные тесты",()=>_=RunTestsAsync("cloud"),icon:"cloud"),
            Action("Два устройства",()=>_=RunTestsAsync("cloud-race"),icon:"devices"),
            Action("Открыть отчёты",()=>OpenData("test-reports"),icon:"file"),
            Action("Тест уведомления",()=>_=SendTestNotificationAsync(),icon:"bell")
        };
        string[] hints=
        [
            "Быстрые проверки логики на изолированных локальных данных; интернет не нужен.",
            "Безопасность KDBX, история, конфликты, восстановление после аварий; без обращения к облаку.",
            "Проверка реального подключения, чтения и записи только в отдельной тестовой папке выбранного облака.",
            "Два независимых тестовых устройства и конкурирующие изменения в отдельной папке облака. Может выполняться несколько минут.",
            "Открыть папку с отчётами выполненных проверок (JSON/XML)."
        ];
        var actionGrid=new Grid{ColumnDefinitions=new ColumnDefinitions("Auto,Auto,Auto,Auto,Auto,*")};
        for(var i=0;i<actions.Length;i++)
        {
            var b=actions[i];
            // Only the four execution buttons are locked while tests run.
            // Reports and the notification smoke test remain available.
            b.IsEnabled=i>=4 || !_testing && !_saving;
            b.Margin=new Thickness(0,0,i==actions.Length-1?0:5,0);
            b.Padding=new Thickness(6,9);
            b.HorizontalAlignment=i==actions.Length-1?
                HorizontalAlignment.Right:HorizontalAlignment.Left;
            if(b.Content is StackPanel iconLabel)
            {
                iconLabel.Spacing=4;
                foreach(var child in iconLabel.Children)
                {
                    if(child is TextBlock caption)caption.FontSize=UiTypography.FootnoteSize;
                    if(child is FluentIcons.Avalonia.FluentIcon glyph)
                    {
                        glyph.Width=14;
                        glyph.Height=14;
                        glyph.FontSize=14;
                    }
                }
            }
            if(i<hints.Length)ToolTip.SetTip(b,UiLang.T(hints[i]));
            Grid.SetColumn(b,i);
            actionGrid.Children.Add(b);
        }
        _diagnosticsActions=actions[..4];
        root.Children.Add(actionGrid);

        var scope=Row(Muted("Облако для сетевых тестов:"));
        // Diagnostics must only offer clouds that have actually been applied to
        // EngineHost. The editor draft may contain uncommitted connections.
        var configuredClouds=_app.Engine?.Current?.Clouds.Select(c=>c.Name).ToList() ?? new List<string>();
        var unsavedClouds=_draft.Clouds.Select(c=>c.Name)
            .Except(configuredClouds,StringComparer.OrdinalIgnoreCase).ToArray();
        if(unsavedClouds.Length>0)
            root.Children.Add(Muted("Не сохранены и недоступны для тестов: "+
                string.Join(", ",unsavedClouds)+". Проверьте настройки этих подключений.",12));
        var cloudOptions=new[]{new ComboBoxItem{Content=UiLang.T("Все облака"),Tag=""}}
            .Concat(configuredClouds.Select(x=>new ComboBoxItem{Content=x,Tag=x})).ToArray();
        var cloudSelector=new ComboBox
        {
            MinWidth=230,
            ItemsSource=cloudOptions,
            SelectedItem=cloudOptions.First(x=>(string)x.Tag! ==
                (_selectedCloud.Length==0 || !configuredClouds.Contains(_selectedCloud,StringComparer.OrdinalIgnoreCase)?"":_selectedCloud)),
            IsEnabled=!_testing
        };
        if(_selectedCloud.Length>0 && !configuredClouds.Contains(_selectedCloud,StringComparer.OrdinalIgnoreCase))_selectedCloud="";
        cloudSelector.SelectionChanged+=(_,_)=>_selectedCloud=
            (cloudSelector.SelectedItem as ComboBoxItem)?.Tag as string ?? "";
        _diagnosticsCloudSelector=cloudSelector;
        scope.Children.Add(cloudSelector);
        root.Children.Add(scope);
        root.Children.Add(Muted("«Облачные тесты» — чтение и запись в изолированной папке. «2 устройства» — конкурентные версии и восстановление. Рабочая KDBX не используется.",12));

        _diagnosticsStatus=Text(_testing?ActivityText():UiLang.T(_lastResult),14,true,_testing?Secondary:Fore);
        _diagnosticsStatus.TextWrapping=TextWrapping.Wrap;
        _diagnosticsStatus.Margin=new Thickness(0,0,12,0);
        var statusRow=new Grid{ColumnDefinitions=new ColumnDefinitions("*,Auto")};
        statusRow.Children.Add(_diagnosticsStatus);
        _diagnosticsCancel=Action("Остановить тест",RequestTestCancellation);
        _diagnosticsCancel.IsEnabled=_testing && _testCancellation is {IsCancellationRequested:false} &&
            (_testModeName is "Два устройства" or "Облачные тесты");
        Grid.SetColumn(_diagnosticsCancel,1);
        statusRow.Children.Add(_diagnosticsCancel);
        root.Children.Add(statusRow);
        _diagnosticsProgress=new ProgressBar{IsIndeterminate=true,Height=5,IsVisible=_testing};
        root.Children.Add(_diagnosticsProgress);
        root.Children.Add(Text("Журнал выполнения",16,true));
        // TextBox cannot color individual test results and mixes line heights
        // when a symbol falls back to another font. Dedicated rows have a fixed
        // 24 px height. The enclosing log frame is intentionally rectangular.
        // Previously saved diagnostic reports remain readable.
        _diagnosticsRows=UiKit.Stack(0);
        _diagnosticsRows.Margin=new Thickness(10,8,10,8);
        _diagnosticsLogScroll=new ScrollViewer
        {
            Content=_diagnosticsRows,MinHeight=300,Height=370,
            HorizontalScrollBarVisibility=ScrollBarVisibility.Auto,
            VerticalScrollBarVisibility=ScrollBarVisibility.Auto,
            Background=new SolidColorBrush(Color.Parse(Dark?"#080E15":"#F6F9FC"))
        };
        root.Children.Add(new Border
        {
            BorderBrush=new SolidColorBrush(Color.Parse(Dark?"#435466":"#C6D3E1")),
            BorderThickness=new Thickness(1),CornerRadius=new CornerRadius(0),
            ClipToBounds=true,Child=_diagnosticsLogScroll
        });
        RenderDiagnosticRows();
        var copyLog=Action("Копировать журнал",()=>_=CopyDiagnosticLogAsync(),icon:"file");
        copyLog.HorizontalAlignment=HorizontalAlignment.Left;
        root.Children.Add(copyLog);
        root.Children.Add(Muted("Подробные технические данные и отчёты доступны через «Открыть отчёты».",12));
        return Wrap(root);
    }

    private async Task SendTestNotificationAsync()
    {
        // Keep invariant event identifiers in the in-memory journal. Never store
        // a translated string: the user can change the UI language at any time.
        AppendTest("[NOTIFY] prepare",false);
        try
        {
            // Only the actual Windows toast body is localized at send time.
            var title=UiLang.T("Тестовое уведомление KeyPassXC Cloud Sync");
            var body=UiLang.T("Это тестовое уведомление. Если вы его видите, Windows показывает системные уведомления KeyPassXC Cloud Sync.");
            await Task.Run(()=>_app.Notifications.Send(this,title,body));
            AppendTest("[PASS] Notification / accepted by the Windows notification service",false);
        }
        catch(Exception e)
        {
            // The UI shows a localized error and a stable diagnostic code;
            // the original OS exception stays in a separate technical file.
            AppendTest("[FAIL] Notification / service rejected the test: "+e.GetType().Name+"|0x"+e.HResult.ToString("X8"),false);
            try
            {
                var logDirectory=Path.Combine(_draft.DataDirectory,"logs");
                Directory.CreateDirectory(logDirectory);
                File.AppendAllText(Path.Combine(logDirectory,"notifications.log"),
                    DateTimeOffset.UtcNow.ToString("O")+" [ERROR] Test notification: "+
                    e.ToString().Replace('\r',' ').Replace('\n',' ')+Environment.NewLine);
            }
            catch(IOException) { /* logging must not mask the notification error */ }
            catch(UnauthorizedAccessException) { /* same */ }
        }
    }

    private void RequestTestCancellation()
    {
        if(!_testing || _testCancellation is not {IsCancellationRequested:false} cancel)return;
        // Paint a visible acknowledgement BEFORE starting potentially blocking
        // cancellation callbacks. Cancellation runs off the UI thread.
        _testCurrentStep="остановка активной операции";
        if(_diagnosticsCancel is not null)_diagnosticsCancel.IsEnabled=false;
        AppendTest("[CANCEL] Остановка запрошена. Прерываю сетевые операции…",false);
        if(_diagnosticsStatus is not null)_diagnosticsStatus.Text=ActivityText();
        _=Task.Run(() =>
        {
            try { cancel.Cancel(); }
            catch(ObjectDisposedException) { /* Test already finished. */ }
            catch(Exception ex)
            {
                QueueTestOutput("[FAIL] Не удалось передать отмену: "+ex.GetType().Name);
            }
        });
    }

    private static string TestModeName(string mode)=>mode switch
    {
        "local"=>"Локальные тесты",
        "safety"=>"Безопасность",
        "cloud"=>"Облачные тесты",
        "cloud-race"=>"Два устройства",
        _=>"Проверка"
    };

    private static string TestTime(TimeSpan time)=>time.TotalHours>=1
        ?$"{(int)time.TotalHours:00}:{time.Minutes:00}:{time.Seconds:00}"
        :$"{time.Minutes:00}:{time.Seconds:00}";

    private string ActivityText()
    {
        var step=_testCancellation?.IsCancellationRequested==true &&
            !_testCurrentStep.StartsWith("очистка",StringComparison.Ordinal)
            ?"остановка и освобождение ресурсов"
            :_testWaitingForGate?"ожидание завершения фоновой синхронизации"
            :_testCurrentStep.Length==0?"подготовка":_testCurrentStep;
        return UiDiagnostics.Activity(_testModeName,step,_testElapsed.Elapsed);
    }

    // This timer is UI-only. It does not poll the network or advance a test.
    // In particular, a pending engine Gate no longer looks like a frozen window.
    private void RefreshDiagnosticsActivity()
    {
        if(!_testing)return;
        if(_diagnosticsStatus is not null)_diagnosticsStatus.Text=ActivityText();
        var elapsed=_testElapsed.Elapsed;
        if(elapsed-_testLastWorkerOutput<TimeSpan.FromSeconds(15) ||
            (_testLastHeartbeat!=TimeSpan.Zero && elapsed-_testLastHeartbeat<TimeSpan.FromSeconds(30)))return;
        _testLastHeartbeat=elapsed;
        var last=(int)Math.Floor((elapsed-_testLastWorkerOutput).TotalSeconds);
        var detail=_testCancellation?.IsCancellationRequested==true
            ?"остановка в процессе; возможно, выполняется очистка тестовых данных"
            :_testWaitingForGate?"жду освобождения движка синхронизации"
            :"нет нового вывода от операции; последний этап: "+_testCurrentStep;
        AppendTest($"[WAIT] {detail} · прошло {TestTime(elapsed)}, без сообщений теста {last} с",false);
    }

    private void QueueTestOutput(string line)
    {
        _pendingTestOutput.Enqueue(line);
        if(Interlocked.Exchange(ref _testOutputDispatchScheduled,1)==0)
            Dispatcher.UIThread.Post(FlushTestOutput);
    }

    private void FlushTestOutput()
    {
        var changed=false;
        while(_pendingTestOutput.TryDequeue(out var line))
        {
            AppendTest(line,updateDisplay:false);
            changed=true;
        }
        if(changed)UpdateDiagnosticsLogView();
        Interlocked.Exchange(ref _testOutputDispatchScheduled,0);
        if(!_pendingTestOutput.IsEmpty && Interlocked.Exchange(ref _testOutputDispatchScheduled,1)==0)
            Dispatcher.UIThread.Post(FlushTestOutput);
    }

    private async Task RunTestsAsync(string mode)
    {
        if(_testing || _saving)return;
        var host=_app.Engine;
        if((mode is "cloud" or "cloud-race") && host?.Current is null)
        {
            _lastResult="Сначала настройте подключение на вкладке «Облака»";
            AppendTest("[FAIL] Cloud diagnostics / saved cloud connection required");
            if(_diagnosticsStatus is not null)_diagnosticsStatus.Text=UiLang.T(_lastResult);
            return;
        }
        if((mode is "cloud" or "cloud-race") && _selectedCloud.Length>0 &&
           !host!.Current!.Clouds.Any(c=>c.Name.Equals(_selectedCloud,StringComparison.OrdinalIgnoreCase)))
        {
            _lastResult="Облако «"+_selectedCloud+"» отсутствует в применённой конфигурации.";
            AppendTest("[FAIL] "+_lastResult+" Сохраните подключения на вкладке «Облака».");
            if(_diagnosticsStatus is not null)_diagnosticsStatus.Text=UiLang.T(_lastResult);
            return;
        }
        _testing=true;
        var testCancel=new CancellationTokenSource();
        _testCancellation=testCancel;
        _testModeName=TestModeName(mode);
        _testCurrentStep="подготовка";
        _testWaitingForGate=host is not null && host.Gate.CurrentCount==0;
        _testElapsed.Restart();
        _testLastWorkerOutput=TimeSpan.Zero;
        _testLastHeartbeat=TimeSpan.Zero;
        _testOutput.Clear();
        _testRawLines.Clear();
        _lastTest="";
        _lastResult="Выполняется: "+_testModeName;
        RefreshCurrentPage();
        AppendTest("[INFO] Запущено: "+_testModeName);
        AppendTest(mode is "cloud" or "cloud-race"
            ?"[INFO] Рабочая KDBX не используется. Обращение к облаку только в отдельной временной тестовой папке."
            :"[INFO] Изолированные локальные данные; подключение к облакам не требуется.");
        bool acquired=false;
        try
        {
            if(host is not null)
            {
                if(_testWaitingForGate)AppendTest("[WAIT] Фоновая синхронизация заняла движок; тест начнётся после её окончания.",false);
                await host.Gate.WaitAsync(testCancel.Token);
                acquired=true;
                _testWaitingForGate=false;
            }
            AppendTest("[INFO] Тестовый сценарий запущен.");
            var cfg=host?.Current;
            var selectedCloud=_selectedCloud;
            var code=await Task.Run(() =>
            {
                V2Diagnostics.Progress=QueueTestOutput;
                try
                {
                    return mode switch
                    {
                        "local"=>V2Diagnostics.Run(cfg),
                        "safety"=>V2Diagnostics.RunSafety(cfg),
                        "cloud" when cfg is not null=>V2Diagnostics.RunCloudIntegration(cfg,selectedCloud,testCancel.Token),
                        "cloud-race" when cfg is not null=>V2Diagnostics.RunCloudRace(cfg,selectedCloud,testCancel.Token),
                        _=>1
                    };
                }
                finally{V2Diagnostics.Progress=null;}
            });
            FlushTestOutput();
            _lastResult=testCancel.IsCancellationRequested?"Тест остановлен":
                code==0?"Проверки успешно завершены":"Проверки завершились с ошибками";
            AppendTest(testCancel.IsCancellationRequested?"[CANCEL] Запуск остановлен; результат не считается PASS.":
                code==0?"[INFO] Все сценарии завершены успешно.":"[INFO] Внимание: один или несколько сценариев завершились с ошибкой.");
        }
        catch(OperationCanceledException) when(testCancel.IsCancellationRequested)
        {
            FlushTestOutput();
            AppendTest("[CANCEL] Сценарий отменён. Результат очистки тестовой папки см. выше в журнале.");
            _lastResult="Тест остановлен";
        }
        catch(Exception e)
        {
            FlushTestOutput();
            AppendTest("[FAIL] "+UiErrorDisplay.Present(e));
            _lastResult="Ошибка выполнения тестов";
        }
        finally
        {
            if(acquired){host!.Gate.Release();host.Wake();}
            _testElapsed.Stop();
            _testing=false;
            _testWaitingForGate=false;
            _testCancellation=null;
            testCancel.Dispose();
            AppendTest("[INFO] Завершено за "+TestTime(_testElapsed.Elapsed)+". Итог: "+_lastResult);
            if(_diagnosticsStatus is not null)_diagnosticsStatus.Text=UiLang.T(_lastResult);
            if(_diagnosticsProgress is not null)_diagnosticsProgress.IsVisible=false;
            foreach(var b in _diagnosticsActions)b.IsEnabled=true;
            if(_diagnosticsCancel is not null)_diagnosticsCancel.IsEnabled=false;
            if(_diagnosticsCloudSelector is not null)_diagnosticsCloudSelector.IsEnabled=true;
        }
    }

    private void AppendTest(string line,bool fromWorker=true,bool updateDisplay=true)
    {
        if(fromWorker && _testing)
        {
            _testLastWorkerOutput=_testElapsed.Elapsed;
            if(line.StartsWith("[CLEANUP] ",StringComparison.Ordinal))_testCurrentStep="очистка: "+line[10..];
            else if(line.StartsWith("[STEP] ",StringComparison.Ordinal))_testCurrentStep=line[7..];
            else if((line.StartsWith("[NET ] ",StringComparison.Ordinal) ||
                     line.StartsWith("[NET] ",StringComparison.Ordinal)) &&
                !_testCurrentStep.StartsWith("очистка",StringComparison.Ordinal) &&
                (line.Contains(" — отправка",StringComparison.Ordinal) ||
                 line.Contains(" — подготовка / OAuth",StringComparison.Ordinal)))_testCurrentStep=line[(line.StartsWith("[NET ] ",StringComparison.Ordinal)?7:6)..];
        }
        _testRawLines.Add((DateTime.Now.ToString("HH:mm:ss"),line));
        // Retain a bounded in-memory event stream for immediate language changes.
        // Raw test reports are written independently by the test runners.
        if(_testRawLines.Count>10000)_testRawLines.RemoveRange(0,1000);
        if(updateDisplay)UpdateDiagnosticsLogView();
    }

    private void UpdateDiagnosticsLogView()
    {
        // The raw event list is never translated in place. A language change
        // constructs fresh rows in the currently selected UI language.
        _lastTest=RenderDiagnosticLines();
        RenderDiagnosticRows();
        if(_testing && _diagnosticsStatus is not null)_diagnosticsStatus.Text=ActivityText();
    }

    private static IBrush DiagnosticBrush(string hex)=>new SolidColorBrush(Color.Parse(hex));

    private Control DiagnosticRow(string timestamp,UiDiagnostics.DisplayLine view)
    {
        var green=DiagnosticBrush(Dark?"#54D59A":"#087D49");
        var red=DiagnosticBrush(Dark?"#FF747E":"#B51C30");
        var yellow=DiagnosticBrush(Dark?"#F9C65C":"#926000");
        var blue=DiagnosticBrush(Dark?"#79B7FF":"#2366A9");
        var dim=DiagnosticBrush(Dark?"#90A5BC":"#60748C");
        IBrush main=view.Kind switch
        {
            UiDiagnostics.LineKind.Passed => green,
            UiDiagnostics.LineKind.Failed => red,
            UiDiagnostics.LineKind.Skipped => yellow,
            UiDiagnostics.LineKind.Waiting => yellow,
            UiDiagnostics.LineKind.Section => blue,
            _ => Fore
        };
        var glyph=view.Kind switch
        {
            UiDiagnostics.LineKind.Passed => "✓",
            UiDiagnostics.LineKind.Failed => "✕",
            UiDiagnostics.LineKind.Skipped => "−",
            UiDiagnostics.LineKind.Waiting => "◷",
            UiDiagnostics.LineKind.Section => "◆",
            UiDiagnostics.LineKind.Summary => "≡",
            _ => ""
        };
        var row=new Grid
        {
            Height=24,ColumnDefinitions=new ColumnDefinitions("88,26,Auto"),
            VerticalAlignment=VerticalAlignment.Center
        };
        row.Children.Add(new TextBlock
        {
            Text=timestamp,FontFamily=UiTypography.DiagnosticMonospace,FontSize=UiTypography.DiagnosticTextSize,
            Foreground=dim,VerticalAlignment=VerticalAlignment.Center,
            TextWrapping=TextWrapping.NoWrap
        });
        var symbol=new TextBlock
        {
            Text=glyph,FontFamily=UiTypography.DiagnosticSymbols,FontSize=UiTypography.DiagnosticSymbolSize,
            Foreground=main,VerticalAlignment=VerticalAlignment.Center,
            TextWrapping=TextWrapping.NoWrap
        };
        Grid.SetColumn(symbol,1);row.Children.Add(symbol);
        var message=new TextBlock
        {
            FontFamily=UiTypography.DiagnosticMonospace,FontSize=UiTypography.DiagnosticTextSize,
            Foreground=main,VerticalAlignment=VerticalAlignment.Center,
            TextWrapping=TextWrapping.NoWrap
        };
        if(view.Kind==UiDiagnostics.LineKind.Summary)
        {
            var labels=UiDiagnostics.SummaryLabels();
            message.Inlines!.Add(new Run(labels.Prefix){Foreground=Fore});
            message.Inlines.Add(new Run(view.Passed+labels.Pass){Foreground=green});
            message.Inlines.Add(new Run("   ·   "){Foreground=dim});
            message.Inlines.Add(new Run(view.Failed+labels.Fail){Foreground=red});
            message.Inlines.Add(new Run("   ·   "){Foreground=dim});
            message.Inlines.Add(new Run(view.Skipped+labels.Skip){Foreground=yellow});
        }
        else message.Text=view.Text;
        Grid.SetColumn(message,2);row.Children.Add(message);
        if(view.Hint is {Length:>0})ToolTip.SetTip(row,view.Hint);
        return row;
    }

    private void RenderDiagnosticRows()
    {
        if(_diagnosticsRows is null)return;
        // Incremental rendering keeps long cloud checks responsive. A language
        // change resets the view and recomputes every row from the RAW events.
        if(_diagnosticsRenderedCount>_testRawLines.Count)
        {
            _diagnosticsRows.Children.Clear();
            _diagnosticsRenderedCount=0;
        }
        var scroll=_diagnosticsLogScroll;
        var following=scroll is null ||
            scroll.Offset.Y>=scroll.Extent.Height-scroll.Viewport.Height-48;
        bool changed=false;
        for(var i=_diagnosticsRenderedCount;i<_testRawLines.Count;i++)
        {
            var (time,raw)=_testRawLines[i];
            var view=UiDiagnostics.View(raw);
            if(view is null)continue;
            // Terminal PASS/FAIL/SKIP lines are appended directly. No placeholder
            // rows or second status message per test are generated.
            _diagnosticsRows.Children.Add(DiagnosticRow(time,view));
            changed=true;
        }
        _diagnosticsRenderedCount=_testRawLines.Count;
        if(changed && following && scroll is not null)
            Dispatcher.UIThread.Post(() =>
            {
                if(ReferenceEquals(scroll,_diagnosticsLogScroll))scroll.ScrollToEnd();
            },DispatcherPriority.Background);
    }

    private string RenderDiagnosticLines()
    {
        if(_testRawLines.Count==0)return UiLang.T(_lastTest);
        _testOutput.Clear();
        foreach(var (timestamp,raw) in _testRawLines)
        {
            var formatted=UiDiagnostics.View(raw);
            if(formatted is null)continue;
            _testOutput.Append(timestamp).Append("  ").AppendLine(formatted.Text);
        }
        return _testOutput.ToString();
    }
    private async Task CopyDiagnosticLogAsync()
    {
        try
        {
            var clipboard=TopLevel.GetTopLevel(this)?.Clipboard;
            if(clipboard is not null)await clipboard.SetTextAsync(RenderDiagnosticLines());
        }
        catch(Exception ex){ShowProblem("Копировать журнал",ex.Message);}
    }

    private string SavedDataDirectory()
    {
        // History and folder-opening commands must agree on the effective
        // persisted directory, even if an unsaved Settings draft exists.
        var dataDirectory=_app.Engine?.Current?.DataDirectory;
        if(string.IsNullOrWhiteSpace(dataDirectory) && File.Exists(_configPath))
            dataDirectory=AppOptions.Load(_configPath).DataDirectory;
        if(string.IsNullOrWhiteSpace(dataDirectory))
        {
            // A brand-new installation has no saved INI yet. History should
            // still open and show empty folders rather than throw. Smoke tests
            // must use an isolated temporary root, never the user's AppData.
            dataDirectory=GuiApp.UiSmokeMode
                ?Path.Combine(Path.GetDirectoryName(Path.GetFullPath(_configPath))!,"data")
                :PlatformServices.Current.Paths.DefaultDataDirectory(_configPath);
        }
        return dataDirectory;
    }
    private void OpenData(string sub)
    {
        try{UiKit.OpenFolder(Path.Combine(SavedDataDirectory(),sub));}
        catch(Exception e){ShowProblem("Открытие каталога",e.Message);}
    }
    // Rows are ordinary buttons, not nested controls: clicking anywhere in a row
    // opens the corresponding local directory. The refresh action is independent.
    private sealed record ProtectedFolderUi(Button Open, TextBlock Count, TextBlock Size);
    private CancellationTokenSource? _protectedDataScan;

    private void CancelProtectedDataScan()
    {
        // The running task disposes its own CTS; cancellation alone is safe here.
        try{_protectedDataScan?.Cancel();}
        catch(ObjectDisposedException){ /* An already completed scan. */ }
        _protectedDataScan=null;
    }

    private static string FileSize(long bytes)
    {
        if(bytes<1024)return bytes.ToString("N0")+" "+UiLang.T("Б");
        double value=bytes;
        string[] units=["Б","КБ","МБ","ГБ","ТБ","ПБ"];
        int index=0;
        while(value>=1024 && index<units.Length-1){value/=1024;index++;}
        return value.ToString(value>=10?"N1":"N2")+" "+UiLang.T(units[index]);
    }

    private async Task RefreshProtectedDataAsync(string directory,
        Dictionary<string,ProtectedFolderUi> rows, TextBlock total, TextBlock warning,
        Button refresh, CancellationTokenSource scan)
    {
        refresh.IsEnabled=false;
        total.Text=UiLang.T("Подсчёт…");
        warning.IsVisible=false;
        foreach(var row in rows.Values)
        {
            row.Count.Text="…";
            row.Size.Text="…";
            ToolTip.SetTip(row.Open,UiLang.T("Открыть папку"));
        }
        try
        {
            // Enumeration and FileInfo.Length are never called on the UI thread.
            var results=await Task.Run(()=>LocalProtectedDataInventory.Scan(directory,scan.Token),scan.Token);
            if(!ReferenceEquals(scan,_protectedDataScan) || _page!=5)return;
            long fileCount=0, bytes=0;
            bool complete=true;
            foreach(var item in results)
            {
                var row=rows[item.Name];
                if(item.Complete)
                {
                    row.Count.Text=item.FileCount.ToString("N0");
                    row.Size.Text=FileSize(item.SizeBytes);
                    fileCount=checked(fileCount+item.FileCount);
                    bytes=checked(bytes+item.SizeBytes);
                }
                else
                {
                    complete=false;
                    row.Count.Text="—";
                    row.Size.Text=UiLang.T("Ошибка");
                    ToolTip.SetTip(row.Open,UiLang.T("Ошибка чтения папки")+": "+UiErrorDisplay.Present(item.Error));
                }
            }
            total.Text=complete
                ?fileCount.ToString("N0")+" "+UiLang.T("файлов")+" · "+FileSize(bytes)
                :UiLang.T("Итог не рассчитан: не все папки доступны");
            warning.IsVisible=!complete;
        }
        catch(OperationCanceledException) { /* Navigation or a newer refresh. */ }
        catch(Exception ex) when(ex is IOException or UnauthorizedAccessException or
            OverflowException or ArgumentException)
        {
            if(ReferenceEquals(scan,_protectedDataScan))
            {
                foreach(var row in rows.Values){row.Count.Text="—";row.Size.Text=UiLang.T("Ошибка");}
                total.Text=UiLang.T("Итог не рассчитан: не все папки доступны");
                warning.Text=UiLang.T("Ошибка чтения папки")+": "+UiErrorDisplay.Present(ex);
                warning.IsVisible=true;
            }
        }
        finally
        {
            if(ReferenceEquals(scan,_protectedDataScan))refresh.IsEnabled=true;
            scan.Dispose();
        }
    }

    private Control BuildInactiveConflictPanel(bool stateKnown)
    {
        // Permanent location for conflict resolution, even before the first
        // conflict. This is deliberately informational; no merge may be started.
        var content=Column(9);
        content.Children.Add(Text("Разрешение конфликтов",18,true));
        content.Children.Add(stateKnown
            ?Text("Активных конфликтов нет",14,true,Good)
            :Muted("Состояние конфликтов ещё не загружено",13));
        content.Children.Add(Muted("При конфликте здесь появятся конкретные версии KDBX и порядок их объединения в KeePassXC. Файлы старых конфликтов могут оставаться в папке «Конфликтные копии».",12));
        var prepare=Action("Подготовить файлы для Merge",()=>_=PrepareMergeAsync(),icon:"merge-prepare");
        var confirm=Action("Подтвердить Merge",()=>_=FinishMergeAsync(),icon:"merge-confirm");
        prepare.IsEnabled=false;
        confirm.IsEnabled=false;
        content.Children.Add(Row(prepare,confirm));
        return Card(content);
    }

    private Control BuildActiveConflictPanel(string baseDir)
    {
        var content=Column(11);
        content.Children.Add(Text("Решение конфликта",18,true));
        content.Children.Add(Text("Требуется объединение версий KDBX",15,true,
            new SolidColorBrush(Color.Parse(Dark?"#F1B85D":"#9B540C"))));
        content.Children.Add(Muted("Синхронизация KDBX ожидает ручного разрешения. Ни одна из конфликтующих версий автоматически не перезаписывается."));
        var versions=ActiveConflictFiles.Read(baseDir,_state!);
        content.Children.Add(Text(UiLang.T("Файлы текущего конфликта")+" ("+versions.Length+")",14,true));
        foreach(var v in versions)
        {
            var detail=new Grid{ColumnDefinitions=new ColumnDefinitions("*,Auto"),
                ColumnSpacing=16,Margin=new Thickness(4,3)};
            var left=Column(3);
            var name=Text(v.FileName,13,true);
            name.TextWrapping=TextWrapping.Wrap;
            left.Children.Add(name);
            if(v.Ready)
            {
                var date=v.CreatedUtc?.ToLocalTime().ToString("yyyy-MM-dd HH:mm")??"—";
                var origin=v.Source switch
                {
                    "local"=>UiLang.T("Изменение на компьютере"),
                    "merge"=>UiLang.T("Результат предыдущего объединения"),
                    { } value when value.StartsWith("external:",StringComparison.Ordinal)
                        =>UiLang.T("Внешнее изменение через облако")+": "+value[9..],
                    _=>UiLang.T("Источник не указан")
                };
                left.Children.Add(Muted(UiLang.T("Дата версии")+": "+date+" · "+origin,12));
            }
            else left.Children.Add(Text(UiLang.T("Проблема")+": "+UiErrorDisplay.Present(v.Error),12,false,
                new SolidColorBrush(Color.Parse(Dark?"#FFB78E":"#A63B1F"))));
            detail.Children.Add(left);
            var size=Text(v.SizeBytes is long bytes?FileSize(bytes):"—",13,false,Secondary);
            size.HorizontalAlignment=HorizontalAlignment.Right;
            Grid.SetColumn(size,1);detail.Children.Add(size);
            content.Children.Add(new Border{Child=detail,
                BorderBrush=new SolidColorBrush(Color.Parse(Dark?"#415365":"#D8E2EF")),
                BorderThickness=new Thickness(0,0,0,1),Padding=new Thickness(6,4,6,8)});
        }
        content.Children.Add(Muted("Только перечисленные версии относятся к текущему конфликту. Папка conflicts также содержит старые копии; не выбирайте файлы наугад.",12));
        content.Children.Add(Muted("1. Подготовьте рабочие копии. 2. Объедините их в KeePassXC и сохраните результат в новый файл или поверх рабочей копии. 3. Проверьте базу и подтвердите Merge.",12));
        var prepareMerge=Action("Подготовить файлы для Merge",()=>_=PrepareMergeAsync(),icon:"merge-prepare");
        var confirmMerge=Action("Подтвердить Merge",()=>_=FinishMergeAsync(),true,icon:"merge-confirm");
        var mergeAvailable=versions.Length>=2 && versions.All(v=>v.Ready);
        prepareMerge.IsEnabled=mergeAvailable;
        confirmMerge.IsEnabled=mergeAvailable;
        ToolTip.SetTip(prepareMerge,UiLang.T("Создать отдельную папку только с текущими конфликтующими версиями"));
        ToolTip.SetTip(confirmMerge,UiLang.T("Подтвердить проверенный объединённый файл, сохранённый из KeePassXC"));
        content.Children.Add(Row(prepareMerge,confirmMerge));
        var card=Card(content);
        card.BorderBrush=new SolidColorBrush(Color.Parse(Dark?"#E1A34B":"#B8701A"));
        card.BorderThickness=new Thickness(2);
        return card;
    }

    private Control BuildHistory()
    {
        var root=Column(17);
        root.Children.Add(Text("История",27,true));
        root.Children.Add(Muted("Резервные копии хранятся отдельно от общей истории версий. Слияние выполняется только в KeePassXC."));
        var baseDir=SavedDataDirectory();
        // The conflict area always occupies the first panel. When there is no
        // active conflict, show a compact explanation and disabled actions; when
        // one appears, replace it with the actionable version-specific panel.
        root.Children.Add(_state?.Conflict==true
            ?BuildActiveConflictPanel(baseDir)
            :BuildInactiveConflictPanel(_state is not null));

        var inventory=Column(0);
        inventory.HorizontalAlignment=HorizontalAlignment.Stretch;
        var header=new Grid{ColumnDefinitions=new ColumnDefinitions("*,Auto"),
            HorizontalAlignment=HorizontalAlignment.Stretch};
        header.Children.Add(Text("Защищённые данные",18,true));
        var rows=new Dictionary<string,ProtectedFolderUi>(StringComparer.Ordinal);
        var total=Text("—",13,true);
        var warning=Muted("Не удалось прочитать одну или несколько папок",12);
        warning.IsVisible=false;
        Button? refresh=null;
        refresh=Action("Обновить",()=>
        {
            CancelProtectedDataScan();
            var scan=new CancellationTokenSource();
            _protectedDataScan=scan;
            _=RefreshProtectedDataAsync(baseDir,rows,total,warning,refresh!,scan);
        },icon:"refresh");
        refresh.Padding=new Thickness(11,6);
        refresh.MinHeight=32;
        Grid.SetColumn(refresh,1);
        header.Children.Add(refresh);
        header.Margin=new Thickness(0,0,0,7);
        inventory.Children.Add(header);

        // The header and EVERY data row are peers with the same column sizes.
        // Never put the entire grid in a Button: Avalonia measures a Button's
        // content by desired width, making * shrink and numbers drift left.
        const string columns="*,90,120,46";
        var titles=new Grid{ColumnDefinitions=new ColumnDefinitions(columns),
            ColumnSpacing=12,Margin=new Thickness(0,3,0,3),
            HorizontalAlignment=HorizontalAlignment.Stretch};
        var categoryTitle=Muted("Категория",12);
        categoryTitle.Margin=new Thickness(12,0,0,0);
        titles.Children.Add(categoryTitle);
        var countTitle=Muted("Файлов",12);
        countTitle.HorizontalAlignment=HorizontalAlignment.Right;
        Grid.SetColumn(countTitle,1);titles.Children.Add(countTitle);
        var sizeTitle=Muted("Размер",12);
        sizeTitle.HorizontalAlignment=HorizontalAlignment.Right;
        Grid.SetColumn(sizeTitle,2);titles.Children.Add(sizeTitle);
        var openTitle=Muted("Папка",12);
        openTitle.HorizontalAlignment=HorizontalAlignment.Center;
        Grid.SetColumn(openTitle,3);titles.Children.Add(openTitle);
        inventory.Children.Add(titles);

        var categories=new (string Folder,string Title,string Description,string Icon)[]
        {
            ("startup-backups","Копии при старте",
                "Копии KDBX перед синхронизацией при запуске, если резервирование включено.","shield"),
            ("backup","Резервные копии",
                "Сохранённые версии KDBX для истории синхронизации и восстановления.","database"),
            ("conflicts","Конфликтные копии",
                "Сохранённые KDBX из текущего и прежних конфликтов; активные версии при необходимости показаны выше.","merge"),
            ("history","История версий",
                "Служебные JSON-описания версий; сами базы хранятся в резервных копиях.","history")
        };
        for(var rowIndex=0;rowIndex<categories.Length;rowIndex++)
        {
            var item=categories[rowIndex];
            // Paint the stripe on the complete four-column row, not on an
            // outer Border whose measured width may cover only the category.
            // This also keeps the row square-edged, as in the other data tables.
            var grid=new Grid{ColumnDefinitions=new ColumnDefinitions(columns),
                ColumnSpacing=12,HorizontalAlignment=HorizontalAlignment.Stretch,
                MinHeight=50,Margin=new Thickness(0,2),
                Name="ProtectedDataTableRow"+rowIndex,
                Background=rowIndex%2==0?ZebraStripeBrush():Brushes.Transparent};
            var nameStack=Column(2);
            nameStack.HorizontalAlignment=HorizontalAlignment.Left;
            var label=UiKit.IconLabel(item.Title,item.Icon);
            label.HorizontalAlignment=HorizontalAlignment.Left;
            nameStack.Children.Add(label);
            var description=Muted(item.Description,12);
            description.Margin=new Thickness(24,0,6,0);
            nameStack.Children.Add(description);
            var folderName=item.Folder;
            var open=new Button{Content=nameStack,Padding=new Thickness(12,4),
                HorizontalContentAlignment=HorizontalAlignment.Left,
                HorizontalAlignment=HorizontalAlignment.Stretch,
                Background=Brushes.Transparent,BorderThickness=new Thickness(0),
                MinHeight=46};
            open.Click+=(_,_)=>OpenData(folderName);
            ToolTip.SetTip(open,UiLang.T("Открыть папку"));
            grid.Children.Add(open);
            var count=Text("…",13);count.HorizontalAlignment=HorizontalAlignment.Right;
            count.VerticalAlignment=VerticalAlignment.Center;
            Grid.SetColumn(count,1);grid.Children.Add(count);
            var size=Text("…",13);size.HorizontalAlignment=HorizontalAlignment.Right;
            size.VerticalAlignment=VerticalAlignment.Center;
            Grid.SetColumn(size,2);grid.Children.Add(size);
            // Use the familiar CLOSED folder outline: the former "folder-open"
            // geometry looked like a tray at 19 px. Keep a small hit target,
            // independently centered within the folder-action column.
            var openFolder=new Button{Content=UiKit.IconGlyph("folder",18),
                Width=30,Height=30,MinWidth=30,MinHeight=30,
                Padding=new Thickness(0),
                Background=new SolidColorBrush(Color.Parse(Dark?"#425267":"#DEE7F1")),
                BorderThickness=new Thickness(0),CornerRadius=new CornerRadius(5),
                HorizontalContentAlignment=HorizontalAlignment.Center,
                VerticalContentAlignment=VerticalAlignment.Center,
                HorizontalAlignment=HorizontalAlignment.Center,
                VerticalAlignment=VerticalAlignment.Center};
            openFolder.Click+=(_,_)=>OpenData(folderName);
            ToolTip.SetTip(openFolder,UiLang.T("Открыть папку"));
            Grid.SetColumn(openFolder,3);grid.Children.Add(openFolder);
            rows.Add(folderName,new ProtectedFolderUi(open,count,size));
            inventory.Children.Add(grid);
        }
        var footer=new Grid{ColumnDefinitions=new ColumnDefinitions("Auto,*"),Margin=new Thickness(12,7,0,0)};
        footer.Children.Add(Text("Всего локально",14,true));
        total.HorizontalAlignment=HorizontalAlignment.Right;
        Grid.SetColumn(total,1);footer.Children.Add(total);
        inventory.Children.Add(footer);
        inventory.Children.Add(warning);
        root.Children.Add(Card(inventory));
        // Headless UI smoke tests validate control construction, not filesystem
        // enumeration. InventoryTests cover the scanner separately; never start
        // a background task when no Avalonia dispatcher loop is running.
        if(!GuiApp.UiSmokeMode)
        {
            var cts=new CancellationTokenSource();
            CancelProtectedDataScan();
            _protectedDataScan=cts;
            _=RefreshProtectedDataAsync(baseDir,rows,total,warning,refresh,cts);
        }
        return Wrap(root);
    }
    private async Task PrepareMergeAsync()
    {
        var host=_app.Engine;
        if(host is null || host.Current is null)return;
        try
        {
            await host.Gate.WaitAsync();
            string path;
            try{path=await Task.Run(()=>new V2Engine(host.Current,new SyncLog(host.Current)).PrepareMerge());}
            finally{host.Gate.Release();}
            UiKit.OpenFolder(path);
            if(_page is 0 or 5)RefreshCurrentPage();
            await MessageDialog.Info(this,"Рабочие копии подготовлены",
                "Рабочие копии подготовлены: после Merge сохраните результат в отдельный KDBX или поверх одной из этих рабочих копий. Оригиналы в data/conflicts/ не изменяйте.");
        }
        catch(Exception e){await MessageDialog.Info(this,"Подготовка Merge",UiErrorDisplay.Present(e));}
    }
    private async Task FinishMergeAsync()
    {
        var host=_app.Engine;
        if(host is null || host.Current is null)return;
        var list=await StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions{Title=UiLang.T("Выберите проверенный объединённый KDBX"),
            AllowMultiple=false,FileTypeFilter=[new FilePickerFileType("KeePass (*.kdbx)"){Patterns=["*.kdbx"]}]});
        if(list.Count==0 || list[0].TryGetLocalPath() is not {Length:>0} file)return;
        // The user may save a verified merged database over a prepared working
        // copy in merge/. Only the immutable originals in conflicts/ are forbidden.
        if(MergeCandidatePaths.IsImmutableOriginal(SavedDataDirectory(),file))
        {
            await MessageDialog.Info(this,"Подтвердить Merge",
                "Нельзя выбирать неизменяемые оригиналы из data/conflicts/. Выберите сохранённый результат Merge, в том числе перезаписанную рабочую копию из data/merge/.");
            return;
        }
        string? matchesOriginal;
        try
        {
            await host.Gate.WaitAsync();
            try{matchesOriginal=new V2Engine(host.Current,new SyncLog(host.Current))
                .InspectMergeCandidate(file);}
            finally{host.Gate.Release();}
        }
        catch(Exception e){await MessageDialog.Info(this,"Ошибка Merge",UiErrorDisplay.Present(e));return;}
        var confirmation=matchesOriginal is null
            ?"Вы открыли и проверили объединённый файл в KeePassXC? KeyPassXC Cloud Sync зарегистрирует его как разрешение конфликта и сохранит исходные версии."
            :"Внимание: выбранная база побайтно совпадает с одной из исходных версий. Это НЕ подтверждённое объединение. Принять выбранную версию, осознанно отказавшись от независимых изменений других веток? Исходные копии сохранятся.";
        if(!await MessageDialog.Ask(this,"Подтвердить Merge",confirmation))return;
        try
        {
            await host.Gate.WaitAsync();
            try{await new V2Engine(host.Current,new SyncLog(host.Current))
                .AcceptMergeAsync(file,true,CancellationToken.None,
                    acceptedOriginalVersionId:matchesOriginal);}
            finally{host.Gate.Release();}
            host.Wake();
            if(_page is 0 or 5)RefreshCurrentPage();
            await MessageDialog.Info(this,"Merge подтверждён","Сохранённая версия будет опубликована после проверки облаков.");
        }
        catch(Exception e){await MessageDialog.Info(this,"Ошибка Merge",UiErrorDisplay.Present(e));}
    }
}
