# Source Selection Algorithm

When the source indexer processes multiple builds for the same assembly (e.g., generic builds, platform-specific builds, or builds with different target frameworks), it uses a scoring algorithm to select the "best" implementation to include in the final source index.

## Overview

The deduplication process groups all compiler invocations by `AssemblyName` and then calculates a score for each build. The build with the highest score is selected and included in the generated solution file.

## Scoring Priorities

The scoring algorithm evaluates builds using the following criteria, ordered by priority from highest to lowest:

### 1. UseForSourceIndex Property (Highest Priority)
- **Score**: `int.MaxValue` (2,147,483,647)
- **Description**: When a project explicitly sets the `UseForSourceIndex` property to `true`, it receives the maximum possible score, ensuring it will always be selected regardless of other factors.
- **Use Case**: Provides an escape hatch for projects that should definitely be included in the source index.

### 2. Platform Support Status (Second Priority)
- **Score**: `-10,000` penalty for platform-not-supported assemblies
- **Description**: If a project has the `IsPlatformNotSupportedAssembly` property set to `true`, it receives a heavy penalty.
- **Use Case**: Ensures that stub implementations containing mostly `PlatformNotSupportedException` are avoided in favor of real implementations.

### 3. Target Framework Version (Third Priority)
- **Score**: `Major * 1000 + Minor * 100`
- **Description**: Newer framework versions receive higher scores. For example:
  - .NET 8.0 = 8,000 + 0 = 8,000 points
  - .NET 6.0 = 6,000 + 0 = 6,000 points
  - .NET Framework 4.8 = 4,000 + 80 = 4,080 points
- **Use Case**: Prefers more recent implementations that are likely to contain the latest features and bug fixes.

### 4. Platform Specificity (Fourth Priority)
- **Score**: `+500` for platform-specific frameworks
- **Additional**: `+100` bonus for Linux platforms, `+50` bonus for Unix platforms
- **Description**: Platform-specific builds (e.g., `net8.0-linux`, `net8.0-windows`) receive bonuses over generic builds.
- **Use Case**: Platform-specific implementations often contain more complete functionality than generic implementations.

### 5. Source File Count (Lowest Priority)
- **Score**: `+1` per source file
- **Description**: Builds with more source files receive higher scores.
- **Use Case**: Acts as a tiebreaker when other factors are equal, assuming more source files indicate a more complete implementation.

## Example Scoring

Consider these hypothetical builds for `System.Net.NameResolution`:

| Build | UseForSourceIndex | IsPlatformNotSupported | Framework | Platform | Source Files | Total Score |
|-------|-------------------|------------------------|-----------|----------|--------------|-------------|
| Generic Build | false | true | net8.0 | none | 45 | -1,955 |
| Linux Build | false | false | net8.0-linux | linux | 127 | 8,727 |
| Windows Build | false | false | net8.0-windows | windows | 98 | 8,598 |
| Override Build | true | false | net6.0 | none | 23 | 2,147,483,647 |

In this example:
- The **Override Build** would be selected due to `UseForSourceIndex=true`
- Without the override, the **Linux Build** would be selected with the highest score
- The **Generic Build** receives a massive penalty for being platform-not-supported

## Implementation Details

The scoring logic is implemented in the `CalculateInvocationScore` method in `BinLogToSln/Program.cs`. The method:

1. Reads project properties from the binlog file
2. Applies scoring rules in priority order
3. Handles parsing errors gracefully
4. Returns a base score of 1 for builds that fail scoring to avoid complete exclusion

## Configuration

The algorithm can be influenced through MSBuild project properties:

- **UseForSourceIndex**: Set to `true` to force selection of this build
- **IsPlatformNotSupportedAssembly**: Set to `true` to indicate this is a stub implementation
- **TargetFramework**: Automatically detected from the project file

These properties are captured from the binlog during the build analysis phase.