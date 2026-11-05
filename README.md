[![dotnet-test](https://github.com/AndanteTribe/EntityAccessAnalyzer/actions/workflows/dotnet-test.yml/badge.svg)](https://github.com/AndanteTribe/EntityAccessAnalyzer/actions/workflows/dotnet-test.yml) [![nuget](https://img.shields.io/nuget/v/AndanteTribe.EntityAccessAnalyzer.svg)](https://www.nuget.org/packages/AndanteTribe.EntityAccessAnalyzer/) [![Releases](https://img.shields.io/github/release/AndanteTribe/EntityAccessAnalyzer.svg)](https://github.com/AndanteTribe/EntityAccessAnalyzer/releases) [![GitHub license](https://img.shields.io/github/license/AndanteTribe/EntityAccessAnalyzer.svg)](./LICENSE)

English | [日本語](./README_JA.md)

## Overview

**EntityAccessAnalyzer** is a C# Roslyn analyzer for architectures where **entity state changes go through service objects**.

This analyzer is built on the design principle that **entities should expose state for reading, while services should provide the public operations that change it**. For projects adopting this approach, it enforces the API boundary by reporting public setters, writable fields, ordinary public methods, and exposed mutable collections on entities as compile-time errors. This helps keep validation and state-transition logic in services instead of allowing callers to modify entities directly.

## Installation

### Requirements

A C# compiler or IDE supporting Roslyn 4.3 or later.

### NuGet

Install the package in the project that defines your entities.

#### .NET CLI

```sh
dotnet add package AndanteTribe.EntityAccessAnalyzer
```

#### Package Manager

```powershell
Install-Package AndanteTribe.EntityAccessAnalyzer
```

## Getting Started

### Select entity namespaces

Specify the namespaces to analyze in a C# file in your project, such as `EntityNamespaces.cs`.

```csharp
[assembly: EntityAccessAnalyzer.EntityAccessScope("App.Entities", "App.Entities.*")]
```

Public classes and structs in matching namespaces are analyzed. Namespace selection supports glob patterns. Keep services outside these namespaces.

### Expose state through entities, changes through services

Place the entity and service below in the same assembly. Callers can read the balance, but the public operation for changing it is `AccountService.Deposit`.

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

`internal` allows the service to update the entity within the same assembly. The analyzer checks the entity's public API; it does not restrict internal access to service classes alone or inspect method bodies for mutations.

### Detect APIs that bypass the service

The following entity exposes direct mutation paths and is rejected:

```csharp
namespace App.Entities
{
    public sealed class Account
    {
        public decimal Balance { get; set; } // EAA0001: callers can assign the balance.

        public void Deposit(decimal amount) => Balance += amount; // EAA0002: operation exposed on the entity.

        public System.Collections.Generic.List<string> Entries { get; } = new(); // EAA0003: callers can modify the list.
    }
}
```

For collections, expose an allowed read-only API such as `IReadOnlyList<T>` and keep mutation internal for use by services. `IEnumerable<T>` alone is also diagnosed: entity collections should expose an allowed collection API rather than only enumeration.

## Diagnostics

All four diagnostics are enabled by default with **Error** severity.

| ID | Rule |
| --- | --- |
| [EAA0001](docs/diagnostics/EAA0001.md) | Do not expose public write access, including setters, writable fields, and writable references. |
| [EAA0002](docs/diagnostics/EAA0002.md) | Do not expose public methods or operators outside the allowed contracts. |
| [EAA0003](docs/diagnostics/EAA0003.md) | Do not expose prohibited types such as mutable collections and delegates. |
| [EAA0004](docs/diagnostics/EAA0004.md) | Do not expose enumeration-only types such as `IEnumerable<T>`. |

Public constructors, `init` accessors, and specified contracts such as `IEquatable<T>` are allowed. See the diagnostic pages for exceptions and examples.

## Analysis Scope

- Public classes and structs (including records) in selected namespaces are analyzed. Nested types are included only when all containing types are also public.
- Internal types and interface, enum, and delegate declarations themselves are excluded.
- Public members are checked, including static members, inherited APIs, and APIs added by generated code. Internal, protected, and other non-public members are excluded.
- Standard members inherited from `System.Object` and explicit interface implementations are allowed. Default interface implementations are excluded.

The analyzer checks public API declarations. It does not guarantee that getters or allowed methods are free of side effects, or that arbitrary referenced objects are immutable.

Examples in the diagnostic pages assume the entity namespace configuration is declared in a separate file. Violation and corrected examples are independent snippets.

## Configuration

To change severity, configure each diagnostic ID in `.editorconfig`:

```editorconfig
[*.cs]
dotnet_diagnostic.EAA0001.severity = error
dotnet_diagnostic.EAA0002.severity = error
dotnet_diagnostic.EAA0003.severity = error
dotnet_diagnostic.EAA0004.severity = error
```

Use `warning` to report warnings or `none` to disable a diagnostic. To suppress a diagnostic in a specific section of code, use `#pragma warning disable` / `#pragma warning restore` with its ID.

## License

This project is licensed under the [MIT License](./LICENSE).
