# Riot Accounts

Windows 11・x64向けの個人用Riotアカウント管理アプリ。アカウント切り替えのログイン補助と、LoLのランク・戦績確認に対応します。

## 起動・初期設定

配布ZIPは、後述の `scripts/publish.ps1` で作成できます。

1. `RiotAccounts-win-x64.zip` をフォルダごと展開し、`RiotAccounts.exe` を起動します。.NETランタイムは同梱しています。
2. 「アカウントを追加」で管理名、ログインID、パスワード、Riot IDの名前とタグ、サーバーを登録します。ログインIDとゲーム内のRiot IDは別項目です。初期サーバーはJP1です。
3. 「設定」でRiotクライアントの `RiotClientServices.exe` を指定します。通常はインストール情報から検出します。
4. LoLの情報を取得する場合は [Riot Developer Portal](https://developer.riotgames.com/) で取得したAPIキーを設定します。開発用キーは24時間で失効します。キーをソースコードや配布ファイルに書き込む必要はありません。

## ログイン

Riotクライアントで別アカウントからログアウトしてから、「Riotにログイン」を押します。クライアントを前面に表示し、入力欄を確認してID・パスワードを入力、ログインを送信します。送信後の認証結果と追加認証はRiotクライアントで確認してください。

入力欄が自動検出できない場合は「設定」→「保存してログイン位置を登録」を使います。ID・パスワードが空のフォームを用意し、ID欄、パスワード欄、ログインボタンの順にマウスを置いてF8を押します。サイズ・DPI・クライアントのバージョンや表示が変わった場合は登録し直してください。安全に入力欄のフォーカスを確認できないクライアントでは自動入力を中止します。

入力中はマウス・キーボードを操作せず、Escで中止できます。ウィンドウや入力先が変わると入力を止めます。「IDをコピー」「パスワードをコピー」から手動でCtrl+Vすることもできます。パスワードのコピーは30秒後、またはアプリ終了時に消去します。その間に別の内容をコピーした場合は消去しません。

## ランク・戦績

- 「全ランク更新」：登録アカウントのSolo/Duo・Flexランク、LP、勝敗を取得します。
- 「戦績・分析更新」：選択中キューの直近20戦／50戦、KDA、CS/分、チャンピオン・ロール別成績を取得します。
- 「ノーマル」を選ぶと、ドラフト・ブラインド・スイフトプレイ・クイックプレイの履歴を日時順にまとめて表示します。「戦績更新」で取得し、各試合の種別も確認できます。ノーマルにはランク・LP推移・参考ランク帯を表示しません。
- 「推移」：更新時点のランク・LPの観測記録です。昇降格を表示し、異なるランク間の線は接続しません。観測間の変化を1試合のLP増減とは扱いません。
- 「次戦の参考ランク帯」：同じキューの直近30日・最大20戦に登場した相手の**取得時点のランク**から中央値・25〜75%の範囲を計算します。10戦以上、相手ランク取得率80%以上が必要です。内部MMRや対戦当時のランクではなく、次戦の相手や勝率を保証するものではありません。

起動時は保存済みの情報を表示します。更新は手動です。API制限で待機中も「中止」が使えます。通信やキーの失効で取得できなくても、保存済みの情報は保持します。リメイクは成績集計から除外します。

## 保存先

`%LOCALAPPDATA%\RiotAccounts\accounts.db` に保存します。ログイン情報・APIキーはWindows DPAPIで暗号化し、登録したWindowsユーザーで復号します。別のユーザーやPCにDBだけを移しても復号できません。試合・ランク情報と管理名はローカルDBに通常データとして保存します。

バックアップ時はアプリを閉じて保存先フォルダ全体をコピーしてください。アカウント削除で、そのアカウントのログイン情報と保存済み分析を削除します。

## 開発・検証

必要環境：.NET 10 SDK。Windows UIの動作検証はWindowsで行います。

```powershell
dotnet test tests/RiotAccounts.Core.Tests -c Release
dotnet build src/RiotAccounts.App -c Release
powershell -ExecutionPolicy Bypass -File scripts/publish.ps1
```

配布版のオフライン診断（ダミーの一時データのみ使用）：

```powershell
$p = Start-Process .\RiotAccounts.exe -ArgumentList '--self-test', 'smoke-results.txt' -Wait -PassThru
Get-Content .\smoke-results.txt
$p.ExitCode
```

実機検証の結果と未実施項目は `docs/windows-validation.md`、ログイン操作の確認手順は `docs/login-validation.md` を参照してください。実APIとの照合には本人の有効なAPIキーとアカウントが必要です。

## 参照

- [Riot API一覧](https://developer.riotgames.com/apis)：Account v1、League v4、Match v5。
- [Riot APIキー](https://developer.riotgames.com/docs/portal#web-apis_api-keys)
- [LoL開発者ポリシー](https://developer.riotgames.com/docs/lol#game-integrity)
- [Windows DPAPI](https://learn.microsoft.com/en-us/dotnet/api/system.security.cryptography.protecteddata)

Riot Gamesの公式アプリではありません。
