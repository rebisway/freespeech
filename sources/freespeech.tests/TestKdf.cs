using System.Runtime.CompilerServices;
using FreeSpeech.Client.Identity;
namespace FreeSpeech.Tests;
internal static class TestKdf
{
    [ModuleInitializer]
    internal static void Init() => IdentityService.Current = new IdentityService.KdfCost(8, 1);
}