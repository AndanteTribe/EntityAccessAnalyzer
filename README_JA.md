[![dotnet-test](https://github.com/AndanteTribe/EntityAccessAnalyzer/actions/workflows/dotnet-test.yml/badge.svg)](https://github.com/AndanteTribe/EntityAccessAnalyzer/actions/workflows/dotnet-test.yml) [![nuget](https://img.shields.io/nuget/v/AndanteTribe.EntityAccessAnalyzer.svg)](https://www.nuget.org/packages/AndanteTribe.EntityAccessAnalyzer/) [![Releases](https://img.shields.io/github/release/AndanteTribe/EntityAccessAnalyzer.svg)](https://github.com/AndanteTribe/EntityAccessAnalyzer/releases) [![GitHub license](https://img.shields.io/github/license/AndanteTribe/EntityAccessAnalyzer.svg)](./LICENSE)

[English](./README.md) | 日本語

## 概要

**EntityAccessAnalyzer**は、**Entityの状態変更をサービスオブジェクト経由に集約する**設計を支援するC#のRoslynアナライザーです。

本アナライザーは、「Entityは状態を読み取るためのAPIを公開し、状態を変更する公開操作はサービスが提供するべきである」という設計思想に基づいています。この方針を採用するプロジェクト向けに、Entityのpublic setter、書き込み可能なフィールド、通常のpublicメソッド、変更可能なコレクションの公開をコンパイルエラーにすることで、API境界を強制します。呼び出し側からの直接変更を防ぎ、検証や状態遷移のロジックをサービスに集約しやすくします。

## インストール

### 動作要件

Roslyn 4.3以降に対応するC#コンパイラーまたはIDEが必要です。

### NuGet

Entityを定義するプロジェクトにパッケージを追加してください。

#### .NET CLI

```sh
dotnet add package AndanteTribe.EntityAccessAnalyzer
```

#### Package Manager

```powershell
Install-Package AndanteTribe.EntityAccessAnalyzer
```

## 使い方

### Entityの名前空間を指定する

プロジェクト内のC#ファイル（例：`EntityNamespaces.cs`）に、解析対象の名前空間を指定してください。

```csharp
[assembly: EntityAccessAnalyzer.EntityAccessScope("App.Entities", "App.Entities.*")]
```

一致する名前空間のpublicなclass・structが解析対象です。名前空間の指定にはglobパターンを使用できます。サービスは対象外の名前空間に配置してください。

### 状態の参照はEntity、変更はサービスから行う

次のEntityとサービスを同じアセンブリに配置してください。この例では、呼び出し側は残高を参照でき、残高を変更する公開操作は`AccountService.Deposit`が提供します。

```csharp
namespace App.Entities
{
    public sealed class Account
    {
        public decimal Balance { get; internal set; }
    }
}

namespace App.Services
{
    public sealed class AccountService
    {
        public void Deposit(App.Entities.Account account, decimal amount)
        {
            if (amount <= 0)
            {
                throw new System.ArgumentOutOfRangeException(nameof(amount));
            }

            account.Balance += amount;
        }
    }
}
```

```csharp
var account = new App.Entities.Account();
var service = new App.Services.AccountService();

service.Deposit(account, 100m);
System.Console.WriteLine(account.Balance);
```

`internal`により、同じアセンブリのサービスから状態を更新できます。アナライザーが検査するのはEntityの公開APIです。internalアクセスの呼び出し元をサービスだけに限定したり、メソッド本体の状態変更を解析したりするものではありません。

### サービスを迂回するAPIを検出する

次のEntityは直接変更する経路を公開しているため、エラーになります。

```csharp
namespace App.Entities
{
    public sealed class Account
    {
        public decimal Balance { get; set; } // EAA0001: 呼び出し側が残高を直接代入できる。

        public void Deposit(decimal amount) => Balance += amount; // EAA0002: Entity自身が操作を公開している。

        public System.Collections.Generic.List<string> Entries { get; } = new(); // EAA0003: 呼び出し側がリストを変更できる。
    }
}
```

コレクションは`IReadOnlyList<T>`などの許可された読み取り専用APIで公開し、変更操作はサービスから利用するinternalなAPIに留めてください。列挙だけを提供する`IEnumerable<T>`も診断対象です。Entityのコレクションには、許可されたコレクションAPIを使用してください。

## 診断一覧

すべて既定で有効で、重大度は**Error**です。

| ID | ルール |
| --- | --- |
| [EAA0001](docs/diagnostics/EAA0001.ja.md) | setter、書き込み可能なフィールド・参照など、publicな書き込み操作を公開しない。 |
| [EAA0002](docs/diagnostics/EAA0002.ja.md) | 許可された契約以外のpublicメソッド・演算子を公開しない。 |
| [EAA0003](docs/diagnostics/EAA0003.ja.md) | 変更可能なコレクションやデリゲートなど、禁止された型を公開しない。 |
| [EAA0004](docs/diagnostics/EAA0004.ja.md) | `IEnumerable<T>`などの列挙専用型を公開しない。 |

publicコンストラクター、`init`アクセサー、`IEquatable<T>`などの指定された契約は許可されます。例外と具体例は各診断ページを参照してください。

## 解析対象

- 対象名前空間のpublicなclass・struct（recordを含む）が対象です。入れ子型は外側の型もすべてpublicの場合に対象です。
- internal型や、interface・enum・delegateの宣言自体は対象外です。
- publicメンバーはstatic、継承したAPI、生成コードが追加したAPIも検査対象です。internal・protectedなどのメンバーは対象外です。
- `System.Object`由来の標準メンバー、明示的インターフェイス実装は許可されます。インターフェイス側のデフォルト実装は対象外です。

検査するのは公開APIの宣言です。getterや許可されたメソッドの副作用、任意の参照先オブジェクトの不変性までは保証しません。

各診断ページのコード例では、対象名前空間の設定を別ファイルに記述しているものとします。違反例と修正例はそれぞれ独立したコードです。

## 設定

重大度を変更する場合は、`.editorconfig`で診断IDごとに指定してください。

```editorconfig
[*.cs]
dotnet_diagnostic.EAA0001.severity = error
dotnet_diagnostic.EAA0002.severity = error
dotnet_diagnostic.EAA0003.severity = error
dotnet_diagnostic.EAA0004.severity = error
```

警告にする場合は`warning`、無効にする場合は`none`を指定してください。特定の箇所だけ抑制するには、対象IDを指定した`#pragma warning disable` / `#pragma warning restore`を使用してください。

## ライセンス

このプロジェクトは[MIT License](./LICENSE)で公開しています。
