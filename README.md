# StoryboardUtilities

A helper library for [storybrew](https://github.com/Damnae/storybrew) osu! storyboard
scripts. It adds a sprite pool, an easing catalog you can slice and resample, a
keyframe to command pipeline, and shorthand aliases for the `Osb*` enums.

## Requirements

- .NET 8 SDK
- A storybrew install (1.96 or newer), for `StorybrewCommon.dll` and `OpenTK.dll`

## Building

The build needs to know where storybrew lives. Pick whichever you prefer:

**A per-machine props file** (gitignored, recommended). Create `Storybrew.props` next to
the `.csproj`:

```xml
<Project>
  <PropertyGroup>
    <StorybrewPath>C:\path\to\storybrew</StorybrewPath>
  </PropertyGroup>
</Project>
```

**An environment variable:**

```
setx STORYBREW_PATH "C:\path\to\storybrew"
```

**Or per invocation:**

```
dotnet build -c Release -p:StorybrewPath="C:\path\to\storybrew"
```

Point it at the folder holding `StorybrewEditor.exe`. If it is wrong the build says so
instead of burying you in `CS0246`.

Output lands in `bin/Release/net8.0/`: `StoryboardUtilities.dll` plus
`StoryboardUtilities.xml`, which carries the doc comments through to IntelliSense.

## Using it in a storybrew project

1. Copy **both** files into your storybrew project folder, next to your `.cs` scripts.
2. In storybrew, open **Settings** and click **Referenced Assemblies**, then add the
   `.dll`. This is recorded in `.sbrew/index.yaml` and is what the script compiler reads.
3. Add `using StoryboardUtilities;` to your script.

For IntelliSense in Visual Studio, `scripts.csproj` needs the reference too:

```xml
<Reference Include="StoryboardUtilities">
  <HintPath>StoryboardUtilities.dll</HintPath>
</Reference>
```

storybrew rewrites `scripts.csproj` on project load and drops anything it did not put
there, so keep a patched copy somewhere it will not be clobbered (`assetlibrary/` works)
and restore it afterwards.

> MSBuild resolves the copy sitting in the project folder ahead of any
> `HintPath` pointing elsewhere, because both assemblies claim `Version=1.0.0.0`. The
> project-local copy is the one that binds, for the IDE and for storybrew alike. Re-copy
> it after every rebuild, or you will spend an afternoon debugging a stale library.

## What's in it

`SpriteAllocator`, `OsbMath`, `OsbCommands`, `Logger`, the `SectionInfo` / `SectionTime` /
`SectionPath` types, and shorthand aliases for the `Osb*` enums. Full walkthrough with
examples: [osu! Storyboarding, Ryoon's helper library](https://ryoon.moe/blog/osu-sb/07-ryoon-helper-lib/).

## License

MIT. storybrew itself is MIT (Copyright (c) 2020 Damnae).
