using Aether.AI.Harness.Skills;

namespace Aether.AI.Harness.Tests;

public class SkillRegistryTests
{
    [Fact]
    public void Register_and_GetAll_returns_content()
    {
        var registry = new SkillRegistry();
        registry.Register("organize-notes", "整理笔记", "笔记技能", "步骤...");

        Assert.True(registry.Contains("organize-notes"));
        var all = registry.GetAll();
        Assert.Single(all);
        Assert.Equal("organize-notes", all[0].Name);
        Assert.Equal("步骤...", all[0].Content);
    }

    [Fact]
    public void ResolveAll_skips_missing_names()
    {
        var registry = new SkillRegistry();
        registry.Register("organize-notes", "整理笔记", "笔记技能", "步骤...");

        var resolved = registry.ResolveAll(["organize-notes", "missing"]);
        Assert.Single(resolved);
        Assert.Equal("organize-notes", resolved[0].Name);
    }

    [Fact]
    public void GetAllMetas_excludes_content()
    {
        var registry = new SkillRegistry();
        registry.Register("organize-notes", "整理笔记", "笔记技能", "正文不应出现在 meta");

        var metas = registry.GetAllMetas();
        Assert.Single(metas);
        Assert.Equal("organize-notes", metas[0].Name);
        Assert.Equal("整理笔记", metas[0].Description);
        Assert.Equal("笔记技能", metas[0].Category);
    }

    [Fact]
    public void LoadFromDirectory_reads_skill_md_files()
    {
        var dir = Path.Combine(Path.GetTempPath(), "aether-skills-test-" + Guid.NewGuid().ToString("N"));
        var skillDir = Path.Combine(dir, "organize-notes");
        Directory.CreateDirectory(skillDir);
        File.WriteAllText(Path.Combine(skillDir, "SKILL.md"),
            "---\nname: organize-notes\ndescription: 整理笔记\ncategory: 笔记技能\n---\n# 整理笔记\n步骤...\n");

        try
        {
            var registry = new SkillRegistry();
            registry.LoadFromDirectory(dir);

            Assert.True(registry.Contains("organize-notes"));
            var all = registry.GetAll();
            Assert.Single(all);
            Assert.Equal("整理笔记", all[0].Description);
            Assert.Contains("步骤", all[0].Content);
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    [Fact]
    public void LoadFromDirectory_missing_dir_is_noop()
    {
        var registry = new SkillRegistry();
        registry.LoadFromDirectory(Path.Combine(Path.GetTempPath(), "does-not-exist-" + Guid.NewGuid().ToString("N")));
        Assert.Empty(registry.GetAll());
    }
}
