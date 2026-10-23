using Ashlar.Manifest.Admission;
using Ashlar.Manifest.Ledger;

// Test-only child: the environment must exist before the runtime starts.
if (args.Length != 2) return 2;
try
{
    for (var i = 0; i < 2; i++)
    {
        if (args[0] == "gate") _ = new GateStore(args[1]);
        else if (args[0] == "ledger") _ = new InstanceLedger(args[1]);
        else return 2;
    }
    Console.WriteLine("STORE_CONSTRUCTED");
    return 0;
}
catch (InvalidOperationException ex)
{
    Console.Error.WriteLine(ex.Message);
    return 42;
}
