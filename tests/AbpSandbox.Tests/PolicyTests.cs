namespace AbpSandbox.Tests;

public class PolicyTests
{
    private static SandboxPolicy Policy() => new(new SandboxOptions
    {
        Images = new() { ["alpine"] = "alpine:3.20@sha256:" + new string('a', 64), ["loose"] = "alpine:3.20" },
    });

    [Fact]
    public void Images_are_named_and_pinned()
    {
        SandboxPolicy policy = Policy();
        Assert.StartsWith("alpine:3.20@sha256:", policy.Image("alpine"));
        Assert.Throws<SandboxRefusedException>(() => policy.Image("ubuntu"));
        Assert.Throws<SandboxRefusedException>(() => policy.Image("loose"));
        Assert.Equal("alpine:3.20", new SandboxPolicy(new SandboxOptions { Images = new() { ["loose"] = "alpine:3.20" }, AllowUnpinnedImages = true }).Image("loose"));
    }

    [Fact]
    public void Limits_default_and_stop_at_the_maximum()
    {
        SandboxPolicy policy = Policy();
        SandboxPolicy.Resolved defaults = policy.Limits(new SandboxLimits());
        Assert.Equal((TimeSpan.FromMinutes(1), 512L * 1024 * 1024, 1d, 128), (defaults.Time, defaults.MemoryBytes, defaults.Cpus, defaults.Processes));
        Assert.Equal(TimeSpan.FromSeconds(5), policy.Limits(new SandboxLimits { Time = TimeSpan.FromSeconds(5) }).Time);
        Assert.Throws<SandboxRefusedException>(() => policy.Limits(new SandboxLimits { Time = TimeSpan.FromHours(1) }));
        Assert.Throws<SandboxRefusedException>(() => policy.Limits(new SandboxLimits { MemoryBytes = 64L * 1024 * 1024 * 1024 }));
        Assert.Throws<SandboxRefusedException>(() => policy.Limits(new SandboxLimits { Cpus = 8 }));
        Assert.Throws<SandboxRefusedException>(() => policy.Limits(new SandboxLimits { Processes = 0 }));
    }

    [Theory]
    [InlineData("../etc/passwd")]
    [InlineData("/etc/passwd")]
    [InlineData("a/../../b")]
    [InlineData("a\\b")]
    [InlineData("a//b")]
    [InlineData("./a")]
    [InlineData("")]
    public void Paths_stay_in_the_work_folder(string path) =>
        Assert.Throws<SandboxRefusedException>(() => SandboxPolicy.Path(path));

    [Fact]
    public void Input_is_capped_and_each_path_given_once()
    {
        SandboxPolicy policy = new(new SandboxOptions { MaxInputBytes = 10, MaxInputFiles = 2 });
        policy.Input([new("a.txt", new byte[5]), new("b/c.txt", new byte[5])]);
        Assert.Throws<SandboxRefusedException>(() => policy.Input([new("a.txt", new byte[6]), new("b.txt", new byte[5])]));
        Assert.Throws<SandboxRefusedException>(() => policy.Input([new("a.txt", []), new("a.txt", [])]));
        Assert.Throws<SandboxRefusedException>(() => policy.Input([new("a", []), new("b", []), new("c", [])]));
    }

    [Fact]
    public void A_preparation_key_is_the_same_for_the_same_request_and_the_owners_own()
    {
        SandboxPreparation prep = new() { Steps = [["sh", "-c", "echo hi"]], Files = [new("b", [2]), new("a", [1])] };
        string key = SandboxPolicy.PreparationKey("img@sha256:1", "agent-1/project-1", prep);
        Assert.Equal(key, SandboxPolicy.PreparationKey("img@sha256:1", "agent-1/project-1", prep with { Files = [new("a", [1]), new("b", [2])] }));
        Assert.NotEqual(key, SandboxPolicy.PreparationKey("img@sha256:1", "agent-1/project-2", prep));
        Assert.NotEqual(key, SandboxPolicy.PreparationKey("img@sha256:2", "agent-1/project-1", prep));
        Assert.NotEqual(key, SandboxPolicy.PreparationKey("img@sha256:1", "agent-1/project-1", prep with { Files = [new("a", [9]), new("b", [2])] }));
        Assert.NotEqual(key, SandboxPolicy.PreparationKey("img@sha256:1", "agent-1/project-1", prep with { Steps = [["sh", "-c", "echo hi "]] }));

        SandboxPreparation shared = prep with { Shared = true };
        Assert.Equal(SandboxPolicy.PreparationKey("img@sha256:1", "a", shared), SandboxPolicy.PreparationKey("img@sha256:1", "b", shared));
    }
}
