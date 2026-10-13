# Web版（自宅サーバー）

常時起動のMacでWeb版を常駐させ、6時間ごとにRiot APIから全アカウントのSolo/Duo・Flex・ノーマルを取得します。閲覧（ブラウザ、Windowsアプリの取得元「Web」）は保存済みのデータを表示するだけで、Riot APIを呼びません。Riot APIを呼ぶのはWeb版だけなので、APIキーの枠を食い合いません。

## 構成

- `src/RiotAccounts.Web`（ASP.NET Core）。`127.0.0.1` だけで待ち受け、`tailscale serve` でtailnet内にだけHTTPSで公開します（Funnelは使いません）。
- 配置先はこのリポジトリの `server/` です（gitignore済み）。
  - `server/app`：実行ファイル（self-contained、osx-arm64）。`server/app.prev` に1つ前のビルドを残します。
  - `server/data`：DB（`accounts.db`）、暗号鍵（`keys/`）、診断ログ（`logs/`）。**`git clean -x` を実行すると消えます。**
  - `server/deploy.env`：環境固有の値（launchdのラベル、ポート、tailnetのホスト名）。リポジトリには含めません。
- launchdエージェント `~/Library/LaunchAgents/<LAUNCHD_LABEL>.plist` で常駐します（`scripts/deploy-web.sh` が `deploy/macos/riot-accounts-web.plist.template` から生成）。ログは `~/Library/Logs/RiotAccountsWeb/server.log` と `server-error.log` です。

## 初回

1. `scripts/deploy-web.sh` を実行します。初回は `server/deploy.env` を作って止まるので、`PUBLIC_HOST`（例：`my-mac.tailXXXX.ts.net`）などを記入して再実行します。
2. Windowsアプリのデータを引き継ぐ場合は、下記「WindowsアプリのDBを移行する」を先に行います。
3. tailnetへの公開を1回だけ設定します（再起動後も残ります）：`tailscale serve --bg --https=<SERVE_PORT> http://127.0.0.1:<WEB_PORT>`
4. ブラウザで `https://<PUBLIC_HOST>:<SERVE_PORT>/Settings` を開き、Riot APIキーを登録します。1分ほどで最初の全件更新が始まります。

## 更新・ロールバック・停止

- 更新：`scripts/deploy-web.sh`（Coreのテスト → publish → 入れ替え → 再起動。`server/data` はそのまま）
- ロールバック：`scripts/deploy-web.sh --rollback`
- 状態：`launchctl print gui/$(id -u)/<LAUNCHD_LABEL>`、`curl http://127.0.0.1:<WEB_PORT>/api/status`
- 停止：`launchctl bootout gui/$(id -u)/<LAUNCHD_LABEL>`。公開の停止：`tailscale serve --https=<SERVE_PORT> off`

## 自動更新の動き

- 前回の全件更新を始めてから6時間後に実行します（`RiotWeb__RefreshIntervalHours`）。1分ごとに時刻で判定するので、スリープから復帰した後もすぐ追いつきます。
- 起動直後は1分待ちます（再起動を繰り返してもAPIを叩き続けないため）。
- APIキーが拒否された（401/403）場合は中断し、次の周期まで待ちます。設定画面でキーを保存し直すとすぐ再開します。取得制限（429の再試行切れ）と通信断は30分後に再試行します。
- 手動の更新（全件・アカウント単位）とWindowsアプリからの依頼も同じワーカーで1件ずつ処理します。同じ内容の依頼は1つにまとめます。
- 各キューの直近50戦を対象にします（`RiotWeb__MatchCount`）。保存済みの試合は取り直しません。

## WindowsアプリのDBを移行する

ランク推移・記録済みの平均ティア・過去の戦績はRiot APIから取り直せないため、Web版を使い始める前に移します。

1. Windowsでアプリを終了します。
2. `%LOCALAPPDATA%\RiotAccounts\accounts.db`（`-wal`・`-shm` があれば一緒に）をMacの一時フォルダにコピーします。元のファイルには書き込みません。
3. コピーから秘密情報を消します：`sqlite3 accounts.db "PRAGMA wal_checkpoint(TRUNCATE); DELETE FROM secrets; VACUUM;"`。`SELECT count(*) FROM secrets;` が0、`PRAGMA integrity_check;` が `ok` であることを確認します。
4. Web版を止めて `server/data/accounts.db` に置き、起動します。
5. 一時コピーを削除します。

アカウントのIDはそのまま引き継がれ、Windowsアプリの取得元「Web」との対応付けに使います。

## 保存とセキュリティ

- Web版が持つ秘密情報はRiot APIキーだけです（ASP.NET Core Data Protectionで暗号化、鍵は `server/data/keys`）。ログインID・パスワードは扱いません。
- キーは設定画面で入力するだけで、画面にもAPIにも返しません。
- 受け付けるホスト名は `localhost`・`127.0.0.1`・tailnetのホスト名だけです。フォームには偽造防止トークンが付きます。
- 診断ログにはキー・PUUID・Riot IDを書きません（経路はIDを伏せた形）。

## Riot APIの制限（2026年10月時点）

- 個人キー：1秒20回・2分100回（リージョンごと）。本人か少人数の非公開用途向けで、上限の引き上げはできません。一般公開には使えません。
- キー単位の「アプリ上限」と、エンドポイント単位の「メソッド上限」があります。エンドポイント別の数値は公開されていないため、設定画面の「Riot APIのレート制限（実測）」でRiotの応答ヘッダーの値を確認できます。
- 429が返ったら `Retry-After` の秒数だけ待ちます。上限を回避するために複数のアプリを登録することは禁止されています。
- 製品の変更はDeveloper Portalの製品ページで申告する決まりです。

## API（Windowsアプリ用）

JSONは `JsonSerializerDefaults.Web`（camelCase）です。失敗時は `{"message": "..."}` を返します。

| メソッド | パス | 内容 |
|---|---|---|
| GET | `/api/status` | 版、キー登録の有無、実行中のジョブ、前回の全件更新、次回予定、レート制限の実測値 |
| GET | `/api/snapshot` | 全アカウントと保存済みデータ |
| GET | `/api/accounts/{id}` | 1アカウントと保存済みデータ |
| PUT | `/api/accounts/{id}` | 表示名・Riot ID・サーバー・メモの追加／変更（IDはそのまま） |
| POST | `/api/refresh` | 更新の依頼（全件／アカウント・キュー・件数／ランクのみ） |
| GET | `/api/jobs/{id}` | 依頼の状態（`queued`・`running`・`succeeded`・`failed`）と進捗 |
