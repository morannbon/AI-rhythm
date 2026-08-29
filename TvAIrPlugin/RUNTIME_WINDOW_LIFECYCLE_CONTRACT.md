# Runtime Window Lifecycle Contract (SDK / Host Contract 1.1.5)

## Purpose

Host-managed Runtime ToolWindow の生死はHostを正本とする。Pluginは `Windows.Get` / `Windows.List` の周期pollingでWindow livenessを推測しない。

## Event

`TvAirEventType.RuntimeWindowLifecycleChanged`

`TvAirEventDto.RuntimeWindowLifecycle` に `TvAirRuntimeWindowLifecycleDto` が設定される。イベントは対象Windowを所有するPluginにだけ配送される。

主な項目:

- `PluginId`
- `WindowInstanceId`
- `WindowDefinitionId`
- `RouteSegment`
- `State`
- `CloseBehavior`
- `BackgroundExecution`
- `Source`
- `SessionDisposed`

## Guaranteed terminal delivery

Host-managed ToolWindowでは、通常の×閉鎖およびHost/API closeで `Closing` を通知する。FormClosed確定時に `Closed` を通知する。FormClosing経路を通らない例外的なFormClosedでも `Closed` を通知する。

`BackgroundExecution=StopWithWindow` のPluginは、自Pluginかつ対象 `WindowInstanceId` の `Closing` または `Closed` を受けた時点を、Windowにだけ紐づくTimer / Task / CancellationTokenSource / 一時stateのterminal契機として使用できる。

## Ownership

Hostが所有するもの:

- Windowの実在/閉鎖判定
- Closing/Closedの正規化
- RuntimeWindowLifecycleChangedの発行

Pluginが所有するもの:

- 自PluginのWindowに紐づくbackground処理
- terminal通知後のTimer/Task/state停止・破棄

## Prohibited fallback

この契約の代替として、Pluginが `Windows.Get` / `Windows.List` を周期実行してliveness監視する実装へ戻してはならない。

## Compatibility

1.1.4以前のPluginには追加イベントであり、既存イベント契約を変更しない。利用するPluginは1.1.5で再ビルドする。
