// Shared material for fixtures, NOT a fixture: the theory skips every directory whose name starts
// with an underscore. A fixture that compiles this file from here compiles code that sits outside
// its own directory — outside the content hash, the analyzer fence and the mutation leg — which is
// exactly the shape the loader refuses.
namespace Corpus.Shared;

public static class InjectedPayload
{
    public static int Resolve(int baseDamage, int armor) => Math.Max(0, baseDamage + armor);
}
