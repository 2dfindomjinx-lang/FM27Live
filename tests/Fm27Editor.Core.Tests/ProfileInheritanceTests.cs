using System.Text.Json.Nodes;
using Fm27Editor.Core.Offsets;

namespace Fm27Editor.Core.Tests;

public class ProfileInheritanceTests
{
    [Fact]
    public void BuiltIn_layouts_resolve_cleanly_and_load_into_OffsetProfile()
    {
        foreach (var layout in new[] { "layouts/macos.json", "layouts/touch-macos.json", "layouts/windows.json" })
        {
            string? json = ProfileJsonResolver.LoadLayoutJson(layout);
            Assert.NotNull(json);
            var profile = OffsetProfile.Load(json!);
            Assert.NotNull(profile.VtableClasses);
            Assert.NotEmpty(profile.VtableClasses!);
            Assert.NotNull(profile.RootSpecs);
            Assert.NotEmpty(profile.RootSpecs!);
        }
    }

    [Fact]
    public void Extends_deep_merges_deletes_nulls_and_expands_symbolic_references()
    {
        var child = """
        {
          "extends": "layouts/macos.json",
          "build": "27.9.9-test-mac",
          "matchBuild": "27.9.9+9999999",
          "buildStringRva": "0x5999000",
          "club": {
            "finance": {
              "vtable": "0x5ABCDEF"
            }
          },
          "gameDate": {
            "crossCheck": null
          }
        }
        """;

        var resolved = OffsetProfile.ResolveJson(child);
        // Symbolic @club.finance.vtable inside club.fields.Balance.objectVtables must pick up overridden value.
        var balanceVt = resolved["club"]!["fields"]!["Balance"]!["objectVtables"]![0]!.GetValue<string>();
        Assert.Equal("0x5ABCDEF", balanceVt);
        // Null override deletes the key from the resolved tree.
        Assert.Null(resolved["gameDate"]!["crossCheck"]);

        var loaded = OffsetProfile.Load(child);
        Assert.Equal("27.9.9-test-mac", loaded.Build);
        Assert.Equal("layouts/macos.json", loaded.Extends);
        Assert.Equal(0x5ABCDEFu, loaded.Club!.Finance!.VtableRva);
        Assert.Null(loaded.GameDate!.CrossCheck);
    }

    [Fact]
    public void Cyclic_extends_and_cyclic_symbolic_references_fail_loudly()
    {
        var dir = Directory.CreateDirectory(Path.Combine(Path.GetTempPath(), $"fm27-layout-cycle-{Guid.NewGuid():N}")).FullName;
        try
        {
            File.WriteAllText(Path.Combine(dir, "a.json"), """{ "extends": "b.json" }""");
            File.WriteAllText(Path.Combine(dir, "b.json"), """{ "extends": "a.json" }""");
            var resolver = ProfileJsonResolver.DirectoryLayoutResolver(dir);
            Assert.Throws<FormatException>(() => ProfileJsonResolver.Resolve("""{ "extends": "a.json" }""", resolver));

            var cyclicRef = """{ "x": "@y", "y": "@x" }""";
            Assert.Throws<FormatException>(() => ProfileJsonResolver.Resolve(cyclicRef));

            var missingRef = """{ "x": "@does.not.exist" }""";
            Assert.Throws<FormatException>(() => ProfileJsonResolver.Resolve(missingRef));
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    [Fact]
    public void Symbolic_ref_to_array_element_works_through_GetByPath()
    {
        var node = ProfileJsonResolver.Resolve("""
        {
          "playerVtables": ["0x1111", "0x2222"],
          "copyVt": "@playerVtables[1]"
        }
        """);
        Assert.Equal("0x2222", node["copyVt"]!.GetValue<string>());
    }
}
