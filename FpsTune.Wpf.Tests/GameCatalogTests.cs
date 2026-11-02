using FpsTune.Wpf.Core;
using Xunit;

namespace FpsTune.Wpf.Tests;

public sealed class GameCatalogTests
{
    [Fact]
    public void Embedded_catalog_keeps_existing_identities_and_finds_new_clients_without_launchers()
    {
        Assert.Equal(35,GameCatalog.All.Count);
        var root=Path.Combine(DlssFeedbackTests.RepoRoot(),"work","shell037","catalog-fixture-"+Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        foreach(var game in GameCatalog.All.TakeLast(5))
        {
            var directory=Path.Combine(root,"XboxGames",game.Directories[0],"Game","Binaries","Win64");Directory.CreateDirectory(directory);
            File.WriteAllText(Path.Combine(directory,game.Executables[0]),"fixture; never executed");
            File.WriteAllText(Path.Combine(directory,"launcher.exe"),"fixture; never executed");
            Assert.True(GamePathService.ContainsGameKeyword(game.Directories[0]));
        }
        var found=GamePathService.FindAllInDirectory(root);
        Assert.Equal(5,found.Count);Assert.DoesNotContain(found,p=>Path.GetFileName(p)=="launcher.exe");
        foreach(var game in GameCatalog.All.TakeLast(5))
        {
            var path=Assert.Single(found.Where(p=>Path.GetFileName(p)==game.Executables[0]));
            Assert.Equal(game.Name,GamePathService.LabelFor(path));Assert.True(GamePathService.IsKnownGameExecutable(path));
        }
        Assert.Equal("三角洲行动",GamePathService.LabelFor(@"C:\Games\DeltaForceClient-Win64-Shipping.exe"));
        Assert.Equal("无畏契约",GamePathService.LabelFor(@"C:\Tencent Games\VALORANT\VALORANT-Win64-Shipping.exe"));
        Assert.Equal("MyGame.exe",GamePathService.LabelFor(@"C:\Games\MyGame.exe"));
    }
}
