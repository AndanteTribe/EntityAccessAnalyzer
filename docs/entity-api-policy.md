# Entity公開APIの共通仕様

EntityAccessAnalyzerは、Entityの公開APIを読み取り中心に制限するC#アナライザーです。

このページと各診断ページを仕様の正本とします。共通の対象範囲・報告方針はこのページ、
個別の判定条件・例外は各診断ページで管理します。

> 実装状況：診断定義と英語・日本語のメッセージを用意しています。検出ロジックは未実装です。
> 以下は合意済みのルールを説明したもので、例中の診断コメントは実装後の期待結果を示します。

## 診断一覧

| ID / English | 日本語 | 内容 | 標準重大度 | 既定で有効 | Code Fix |
| --- | --- | --- | --- | --- | --- |
| [EAA0001](diagnostics/EAA0001.md) | [日本語](diagnostics/EAA0001.ja.md) | publicな書き込み操作 | Error | はい | なし |
| [EAA0002](diagnostics/EAA0002.md) | [日本語](diagnostics/EAA0002.ja.md) | 許可されていないpublicメソッド・演算子 | Error | はい | なし |
| [EAA0003](diagnostics/EAA0003.md) | [日本語](diagnostics/EAA0003.ja.md) | 禁止された型の公開 | Error | はい | なし |
| [EAA0004](diagnostics/EAA0004.md) | [日本語](diagnostics/EAA0004.ja.md) | 列挙専用の型の公開 | Error | はい | なし |

カテゴリはすべて`EntityAccessAnalyzer`です。

## 対象となるコード

Source Generatorが生成する`EntityAccessAnalyzer.EntityAccessScopeAttribute`をアセンブリに指定します。
属性は利用側アセンブリ内のinternal型で、実行時のライブラリ参照は不要です。

```csharp
using EntityAccessAnalyzer;

[assembly: EntityAccessScope("App.Entities", "App.Entities.*")]
```

この例では`App.Entities`と、その配下の名前空間にある公開型が対象です。
完全一致と配下のパターンは別々に指定します。

各診断ページのコード例は、この設定を別ファイルに記述したものとします。
違反例と修正例はそれぞれ独立したコードです。

- 対象はpublicなclass、struct、record class、record structです。
- 入れ子型は、その型から外側の型まで、すべてpublicの場合に対象になります。
- internal型や、interface・enum・delegateの宣言自体は対象外です。
- publicメンバーはstaticも含めて検査します。internalやprotectedなどのメンバーは許可します。
- 継承した公開APIと、生成コードが追加した公開APIも対象です。
- `new`で隠された基底型の公開APIは対象外です。
- `System.Object`由来の標準メンバーは許可します。
- 明示的インターフェイス実装は許可します。インターフェイス側のデフォルト実装は対象外です。

`new`で隠された基底メンバーへのキャスト経由のアクセスや、明示的実装の
インターフェイス経由の呼び出しは許容します。
インターフェイスの契約をEntity自身のpublicメンバーとして実装した場合は、通常の規則で検査します。

## 診断の読み方

メッセージにはEntity名と問題のメンバー名が表示されます。型に関する診断では問題の型名も表示されます。
継承・生成されたAPIが原因の場合は、Entity側の型宣言や基底型の指定位置に報告し、原因の宣言元を示します。
元の宣言位置を取得できる場合は追加情報として付与します。
直接宣言された違反は、該当メンバーやアクセサーに報告します。
継承・生成の別によって診断IDを分けず、違反理由が同じなら同じIDを使います。

独立した違反は同時に報告します。例えば、`public List<int> Values;`には、書き込み可能なフィールドとして
EAA0001、禁止された公開型としてEAA0003が該当します。`readonly`に変えるだけではEAA0003は解消しません。
一方、解析経路が異なることによる、同じメンバーの同じ違反の重複報告は避けます。

## 設定

`.editorconfig`で診断ごとに重大度を変更できます。

```editorconfig
[*.cs]
dotnet_diagnostic.EAA0001.severity = error
dotnet_diagnostic.EAA0002.severity = error
dotnet_diagnostic.EAA0003.severity = error
dotnet_diagnostic.EAA0004.severity = error
```

`warning`で警告、`none`で無効にできます。限定した箇所を抑制する場合は、対象IDを指定した
`#pragma warning disable` / `#pragma warning restore`も使用できます。

診断メッセージはUIカルチャーに応じて日本語または既定の英語で表示します。

## 保証する範囲

このアナライザーは公開APIの宣言を検査します。getterや許可されたメソッドの副作用、
任意の参照先オブジェクトの不変性までは証明しません。

`internal`のアクセス境界は名前空間ではなくアセンブリです。同一アセンブリや
`InternalsVisibleTo`の指定先からの変更操作は許容します。

## 関連資料

- [未リリースの診断](../src/EntityAccessAnalyzer/AnalyzerReleases.Unshipped.md)
- [リリース済みの診断](../src/EntityAccessAnalyzer/AnalyzerReleases.Shipped.md)
