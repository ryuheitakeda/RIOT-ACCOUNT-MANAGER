using System.ComponentModel;
using System.Diagnostics;
using System.Net.Http;
using System.Security.Cryptography;
using System.Runtime.InteropServices;

namespace RiotAccounts.App;
public sealed record AccountItem(RiotAccount Account,AccountOverview Overview)
{
    public string Label=>Account.Label;
    public string RiotId=>Account.RiotId;
    public string Platform=>Account.Lol.Platform;
    public string Summary=>$"{Overview.Rank}  ·  {Overview.LastPlayed}";
}
public partial class MainWindow:Window
{
    private readonly Store store;
    private readonly string folder;
    private readonly HttpClient http=new(new HttpClientHandler{AllowAutoRedirect=false}){Timeout=TimeSpan.FromSeconds(30)};
    private readonly RiotApi api;
    private readonly LolStatsProvider riotProvider;
    private readonly OpggApi opggApi;
    private readonly OpggStatsProvider opggProvider;
    private readonly DiagnosticLog log;
    private readonly StatsDiagnostics diagnostics;
    private readonly IGameStatsProvider riotLogged;
    private readonly IGameStatsProvider opggLogged;
    private bool UsingOpgg=>store.Read<string>("setting","statsSource")=="opgg";
    private IGameStatsProvider Provider=>UsingOpgg?opggLogged:riotLogged;
    private readonly NativeLogin login;
    private CancellationTokenSource? operation;
    private bool initialized;
    private Dictionary<Guid,AccountOverview> overviews=[];
    private RiotAccount? Selected=>(AccountsList.SelectedItem as AccountItem)?.Account;
    private string Queue=>QueuePicker.SelectedValue as string??Queues.Solo;
    public MainWindow(Store store,string folder)
    {
        this.store=store;this.folder=folder;
        log=new(Path.Combine(folder,"logs","diagnostics.log"));
        api=new(http,()=>store.GetSecret("riot-api-key"),log);riotProvider=new(store,api);opggApi=new(http,null,log);opggProvider=new(store,opggApi);login=new(store,log);
        riotLogged=new LoggedStatsProvider(riotProvider,"Riot API",log);opggLogged=new LoggedStatsProvider(opggProvider,"OP.GG",log);
        diagnostics=new(api,opggApi,()=>store.GetSecret("riot-api-key"),log);
        InitializeComponent();QueuePicker.ItemsSource=Queues.Definitions;QueuePicker.SelectedValue=Queues.Solo;initialized=true;Reload();
        Closing+=(_,e)=>{if(operation!=null){operation.Cancel();e.Cancel=true;StatusText.Text="処理を中止しています。終了後にもう一度閉じてください。";}};
        Closed+=(_,_)=>{ClipboardLease.ClearOwned();api.Dispose();opggApi.Dispose();http.Dispose();};
    }
    // Searching only filters, so it reuses the overviews instead of reading every cache per keystroke.
    private void Reload(Guid? select=null,bool keepOverviews=false)
    {
        var selectedId=select??Selected?.Id;var search=Search.Text.Trim();var accounts=store.Accounts();
        if(!keepOverviews||accounts.Any(a=>!overviews.ContainsKey(a.Id))){var now=DateTimeOffset.Now;overviews=accounts.ToDictionary(a=>a.Id,a=>Analytics.Overview(store.Cache(a.Id),now));}
        var list=accounts.Where(a=>a.Label.Contains(search,StringComparison.CurrentCultureIgnoreCase)||a.RiotId.Contains(search,StringComparison.CurrentCultureIgnoreCase)).Select(a=>new AccountItem(a,overviews[a.Id])).ToList();
        AccountsList.ItemsSource=list;AccountsList.SelectedItem=list.FirstOrDefault(a=>a.Account.Id==selectedId)??list.FirstOrDefault();Render();UpdateAccountReorderingAvailability();
    }
    private void Render()
    {
        if(!initialized)return;
        var a=Selected;EmptyPanel.Visibility=a==null?Visibility.Visible:Visibility.Collapsed;DetailPanel.Visibility=a==null?Visibility.Collapsed:Visibility.Visible;if(a==null)return;
        AccountTitle.Text=a.Label;var seen=store.Cache(a.Id);var last=new[]{seen.Ranks.LastOrDefault()?.ObservedAt,seen.MatchesUpdatedAt}.Max();
        AccountIdentity.Text=$"{a.RiotId}  /  {a.Lol.Platform}  /  取得元 {(UsingOpgg?"OP.GG（非公式）":"Riot API")}  /  最終取得 {(last is{} t?t.LocalDateTime.ToString("MM/dd HH:mm"):"未取得")}";
        var ranked=Queues.Get(Queue).IsRanked;
        if(!ranked&&HistoryTab.IsSelected)DetailTabs.SelectedItem=OverviewTab;
        RankedSummaryPanel.Visibility=ForecastPanel.Visibility=HistoryTab.Visibility=ranked?Visibility.Visible:Visibility.Collapsed;
        NormalModeText.Visibility=ranked?Visibility.Collapsed:Visibility.Visible;
        RefreshAnalysisButton.Content=ranked?"戦績・分析更新":"戦績更新";
        var cache=store.Cache(a.Id);var latest=cache.Ranks.LastOrDefault();var rank=latest?.Entries.FirstOrDefault(e=>e.QueueType==Queue);
        if(ranked)
        {
        RankTitle.Text=latest==null?"ランク情報を取得してください":rank?.Display??"UNRANKED";
        RankRecord.Text=rank?.Record??(latest==null?"「全ランク更新」または「戦績・分析更新」で取得します。":"このキューのランク情報はありません。");
        RankTime.Text=latest==null?"未取得":$"観測日時 {latest.ObservedAt.LocalDateTime:yyyy/MM/dd HH:mm}";
        var forecast=cache.Forecasts.LastOrDefault(f=>f.QueueType==Queue);
        ForecastRange.Text=forecast?.Lower==null?"データ不足":$"{forecast.Lower} 〜 {forecast.Upper}";
        ForecastDetails.Text=forecast==null?"「戦績・分析更新」で相手ランクを取得します。直近30日の10戦以上が必要です。":$"{forecast.Explanation}\n{forecast.MatchCount}戦 / 相手 {forecast.KnownPlayers}/{forecast.TotalPlayers}件 / 欠測率 {(forecast.TotalPlayers==0?100:100.0*(forecast.TotalPlayers-forecast.KnownPlayers)/forecast.TotalPlayers):F1}%\n取得 {forecast.CreatedAt.LocalDateTime:yyyy/MM/dd HH:mm}"+(forecast.Median==null?"":$"\n中央値 {forecast.Median}");
        if(forecast?.OldestRankObservedAt is{} oldest&&forecast.LatestRankObservedAt is{} newest)
            ForecastDetails.Text+=$"\n相手ランク観測 {oldest.LocalDateTime:MM/dd HH:mm} 〜 {newest.LocalDateTime:MM/dd HH:mm}";
        }
        else
        {
            RankTitle.Text=RankRecord.Text=RankTime.Text=ForecastRange.Text=ForecastDetails.Text="";
            HistoryList.ItemsSource=Array.Empty<string>();HistoryPlot.Plot.Clear();HistoryPlot.Refresh();
        }
        var recent=Analytics.Recent(cache,a.Lol.Puuid??"",Queue,CountPicker.SelectedIndex==1?50:20);
        var summary=Analytics.Summarize(recent,a.Lol.Puuid??"",_=>"全体").FirstOrDefault();
        PerformanceText.Text=summary==null?"まだ戦績がありません。":$"{summary.Games}戦  {summary.Wins}勝 {summary.Games-summary.Wins}敗  /  勝率 {summary.WinRate:F1}%\nKDA {summary.Kda:F2}    CS/分 {summary.CsPerMinute:F1}    平均視界スコア {summary.VisionPerGame:F1}";
        UpdatedText.Text=cache.QueueUpdatedAt.TryGetValue(Queue,out var updated)?$"戦績取得 {updated.LocalDateTime:yyyy/MM/dd HH:mm}  /  リメイクは集計対象外":"このキューの戦績は未取得です。";
        MatchesGrid.ItemsSource=recent.Select(m=>{var p=m.Participants.Single(p=>p.Puuid==a.Lol.Puuid);return new{When=m.StartedAt.LocalDateTime.ToString("MM/dd HH:mm"),Mode=Queues.MatchName(m.QueueId),Result=p.Win?"勝利":"敗北",Champion=p.Champion,Kda=$"{p.Kills} / {p.Deaths} / {p.Assists}",Cs=(p.Cs/(m.DurationSeconds/60.0)).ToString("F1")};}).ToList();
        ChampionStats.ItemsSource=Analytics.Summarize(recent,a.Lol.Puuid??"",p=>p.Champion);
        RoleStats.ItemsSource=Analytics.Summarize(recent,a.Lol.Puuid??"",p=>string.IsNullOrEmpty(p.Role)?"不明":p.Role);
        if(ranked)DrawHistory(cache);
    }
    private void DrawHistory(AccountCache cache)
    {
        HistoryPlot.Plot.Clear();
        var points=cache.Ranks.Select(s=>(At:s.ObservedAt,Rank:s.Entries.FirstOrDefault(r=>r.QueueType==Queue))).ToList();
        var dates=new List<DateTime>();var values=new List<double>();string? current=null;
        void Flush(){if(dates.Count>0){var line=HistoryPlot.Plot.Add.Scatter(dates.ToArray(),values.ToArray());line.LegendText=current??"";line.MarkerSize=5;}dates.Clear();values.Clear();}
        foreach(var point in points)
        {
            var label=point.Rank==null?null:$"{point.Rank.Tier} {point.Rank.Rank}";
            if(label!=current){Flush();current=label;}
            if(point.Rank!=null){dates.Add(point.At.LocalDateTime);values.Add(point.Rank.LeaguePoints);}
        }
        Flush();HistoryPlot.Plot.Axes.DateTimeTicksBottom();HistoryPlot.Plot.YLabel("LP");HistoryPlot.Plot.Axes.AutoScale();HistoryPlot.Refresh();
        HistoryList.ItemsSource=points.Select((p,i)=>
        {
            var before=i>0?points[i-1].Rank?.Order:null;
            var after=p.Rank?.Order;
            var change=before.HasValue&&after.HasValue&&before!=after?(after>before?"  ↑ 昇格":"  ↓ 降格"):"";
            return $"{p.At.LocalDateTime:yyyy/MM/dd HH:mm}   {p.Rank?.Display??"UNRANKED"}{change}";
        }).Reverse().ToList();
    }
    // Never switches the source by itself; only points at the diagnosis and the other source.
    private string SourceHint(Exception ex)
    {
        if(ex is not RiotApiException{StatusCode:not System.Net.HttpStatusCode.NotFound})return "";
        var other=UsingOpgg?"Riot API":"OP.GG";
        return $" 「接続を診断」で失敗した段階を確認できます。設定で取得元を{other}に切り替えることもできます。";
    }
    private async void Diagnose_Click(object sender,RoutedEventArgs e)
    {
        if(Selected is not{} a){StatusText.Text="診断するアカウントを選んでください。";return;}
        var source=UsingOpgg?"opgg":"riot";
        await Run(async(progress,ct)=>
        {
            var report=await diagnostics.RunAsync(a,source,progress,ct);
            progress.Report(report.Ok?"接続を診断しました。すべての段階に成功しました。":$"接続を診断しました。「{report.Failed!.Name}」で失敗しました。");
            new DiagnosticsDialog(report,log.FilePath){Owner=this}.ShowDialog();
        });
    }
    private async Task Run(Func<IProgress<string>,CancellationToken,Task> action)
    {
        if(operation!=null){StatusText.Text="処理中です。終了するか「中止」を押してください。";return;}
        operation=new();UpdateAccountReorderingAvailability();CancelButton.Visibility=Visibility.Visible;
        var status=new StatusProgress(StatusText);
        try{await action(status,operation.Token);}
        catch(OperationCanceledException){StatusText.Text=operation.IsCancellationRequested?"処理を中止しました。取得済みのデータは保存されています。":"処理が中止またはタイムアウトしました。保存済みデータは保持しています。";}
        catch(HttpRequestException){StatusText.Text="通信できません。接続を確認してください。保存済みデータは保持しています。";}
        catch(CryptographicException){StatusText.Text="ログイン情報を復号できません。登録したWindowsユーザーで起動してください。";}
        catch(Microsoft.Data.Sqlite.SqliteException){StatusText.Text="データを保存できません。保存先の空き容量・アクセス権を確認してください。";}
        catch(System.Text.Json.JsonException){StatusText.Text="受信データまたは設定ファイルを読み取れません。設定を確認して再試行してください。";}
        catch(Exception ex) when(ex is RiotApiException or InvalidOperationException or ArgumentException or System.ComponentModel.Win32Exception or System.Windows.Automation.ElementNotAvailableException or ExternalException)
        {StatusText.Text=ex is RiotApiException or InvalidOperationException?ex.Message+SourceHint(ex):"操作に失敗しました。入力内容・Windows権限を確認してください。";}
        finally{status.Active=false;operation.Dispose();operation=null;CancelButton.Visibility=Visibility.Collapsed;Reload();}
    }
    // Progress<T> posts asynchronously, so a report queued just before a synchronous failure
    // would overwrite the error message. Apply UI-thread reports immediately and drop late ones.
    private sealed class StatusProgress(TextBlock target):IProgress<string>
    {
        public bool Active{get;set;}=true;
        public void Report(string message)
        {
            if(target.Dispatcher.CheckAccess()){if(Active)target.Text=message;}
            else target.Dispatcher.BeginInvoke(()=>{if(Active)target.Text=message;});
        }
    }
    private void Search_Changed(object sender,TextChangedEventArgs e){if(initialized)Reload(keepOverviews:true);}
    private void Account_Changed(object sender,SelectionChangedEventArgs e)=>Render();
    private void Analysis_Changed(object sender,SelectionChangedEventArgs e)=>Render();
    private void Add_Click(object sender,RoutedEventArgs e)=>Edit(null);
    private void Edit_Click(object sender,RoutedEventArgs e){if(Selected is{} a)Edit(a);}
    private void Edit(RiotAccount? account)
    {
        if(operation!=null)return;
        try
        {
            var dialog=new AccountDialog(store,account){Owner=this};
            if(dialog.ShowDialog()==true)Reload(dialog.SavedId);
        }
        catch(Exception ex) when(ex is CryptographicException or Microsoft.Data.Sqlite.SqliteException or InvalidOperationException)
        {StatusText.Text="登録情報を開けません。保存先と、登録したWindowsユーザーを確認してください。";}
    }
    private void Delete_Click(object sender,RoutedEventArgs e)
    {
        if(operation!=null||Selected is not{} a)return;
        if(MessageBox.Show(this,$"「{a.Label}」のログイン情報と保存済み戦績を削除します。","アカウントを削除",MessageBoxButton.OKCancel,MessageBoxImage.Question)==MessageBoxResult.OK)
        {
            try{store.Delete(a.Id);Reload();}
            catch(Microsoft.Data.Sqlite.SqliteException){StatusText.Text="削除できませんでした。保存先のアクセス権を確認してください。";}
        }
    }
    private async void Login_Click(object sender,RoutedEventArgs e)
    {
        if(Selected is{} a)await Run((progress,ct)=>login.LoginAsync(store.Credentials(a.Id),progress,ct));
    }
    private void CopyUser_Click(object sender,RoutedEventArgs e){if(Selected is{} a)Copy(a,false);}
    private void CopyPassword_Click(object sender,RoutedEventArgs e){if(Selected is{} a)Copy(a,true);}
    private void OpenOpgg_Click(object sender,RoutedEventArgs e)
    {
        if(Selected is not{} a)return;
        try{Process.Start(new ProcessStartInfo(OpggRegions.ProfileUrl(a.Lol).AbsoluteUri){UseShellExecute=true});StatusText.Text="OP.GGをブラウザで開きました。";}
        catch(ArgumentException ex){StatusText.Text=ex.Message;}
        catch(Win32Exception){StatusText.Text="ブラウザを起動できませんでした。";}
    }
    private void Copy(RiotAccount account,bool password)
    {
        try
        {
            var c=store.Credentials(account.Id);ClipboardLease.Copy(password?c.Password:c.Username,password);
            StatusText.Text=password?"パスワードをコピーしました。30秒後にクリップボードを消去します。":"ログインIDをコピーしました。";
        }
        catch(Exception ex) when(ex is CryptographicException or ExternalException or InvalidOperationException){StatusText.Text="コピーできませんでした。再試行してください。";}
    }
    private async void RefreshAll_Click(object sender,RoutedEventArgs e)=>await Run(async(progress,ct)=>
    {
        var accounts=store.Accounts();if(accounts.Count==0){progress.Report("アカウントを追加してください。");return;}
        for(var i=0;i<accounts.Count;i++){progress.Report($"ランクを更新中 {i+1}/{accounts.Count} — {accounts[i].Label}");await Provider.RefreshRanksAsync(accounts[i],ct,progress);}
        progress.Report("全アカウントのランクを更新しました。");
    });
    private async void RefreshAnalysis_Click(object sender,RoutedEventArgs e)
    {
        if(Selected is{} a){var queue=Queue;var count=CountPicker.SelectedIndex==1?50:20;await Run((progress,ct)=>Provider.RefreshAnalysisAsync(a,queue,count,progress,ct));}
    }
    private void Cancel_Click(object sender,RoutedEventArgs e)=>operation?.Cancel();
    private void Settings_Click(object sender,RoutedEventArgs e)
    {
        if(operation!=null)return;
        try
        {
            string? clientPath;
            try{clientPath=login.ClientPath();}catch(Exception ex) when(ex is System.Text.Json.JsonException or IOException){clientPath=null;}
            var dialog=new SettingsDialog(store,clientPath,folder){Owner=this};
            dialog.ShowDialog();Render();
            if(dialog.SetupRequested)
            {
                var setup=new LoginSetupDialog(store,login,clientPath){Owner=this};
                setup.ShowDialog();
                StatusText.Text=setup.Completed ? "自動入力の設定を確認しました。「Riotにログイン」から利用できます。" : "設定の案内を閉じました。";
            }
        }
        catch(Exception ex) when(ex is CryptographicException or Microsoft.Data.Sqlite.SqliteException)
        {StatusText.Text="設定を開けません。保存先と、登録したWindowsユーザーを確認してください。";}
    }
}
