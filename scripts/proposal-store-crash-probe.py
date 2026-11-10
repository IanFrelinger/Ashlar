#!/usr/bin/env python3
"""Run only inside the devtest container: kill writers at real persistence boundaries.

The probe links the four production proposal-store sources directly, with no copied implementation.
A separate OS process publishes a marker and blocks; SIGKILL bypasses all cleanup/finally code.
A new process must recover the expected decision. Both supported runtimes are exercised.
"""
import argparse
import json
from pathlib import Path
import subprocess
import tempfile
import time
from xml.sax.saxutils import escape

ROOT = Path(__file__).resolve().parents[1]
SOURCES = ("ChangeProposalStore.cs", "ChangeProposal.cs", "ChangeProposalStatus.cs", "IChangeProposalStore.cs")
PROGRAM = r'''
using System.Text.Json;
using Ashlar.BackgroundAgents.Forge;
var root = args[1];
var stage = args.Length > 2 ? args[2] : "";
var mode = args[0];
if (mode.StartsWith("race-")) {
    File.WriteAllText(Path.Combine(root, "ready-" + Environment.ProcessId), "ready");
    while (!File.Exists(Path.Combine(root, "start"))) Thread.Sleep(10);
}
var store = new ChangeProposalStore(root, current => {
    if (mode.StartsWith("race-") && current.EndsWith(".writing")) Thread.Sleep(100);
    if (current == stage) {
        File.WriteAllText(Path.Combine(root, "boundary.marker"), current);
        Thread.Sleep(Timeout.Infinite);
    }
});
try {
    if (mode.EndsWith("add")) store.Add(new ChangeProposal {
        Id = "p", TargetPath = "src/X.cs", NewContent = "content", Summary = "probe" });
    else if (mode.EndsWith("approve")) store.Approve("p", "alice", "approved");
    else if (mode.EndsWith("reject")) store.Reject("p", "bob", "rejected");
    else Console.WriteLine(JsonSerializer.Serialize(store.List()));
    return 0;
} catch (InvalidOperationException) when (mode.StartsWith("race-")) { return 3; }
'''


def race_probe(command, root, modes):
    root.mkdir()
    if modes[0] != "race-add":
        subprocess.run([*command, "add", str(root)], check=True, timeout=30)
    processes = [subprocess.Popen([*command, mode, str(root)], stdout=subprocess.PIPE, stderr=subprocess.PIPE) for mode in modes]
    try:
        deadline = time.monotonic() + 30
        while len(list(root.glob("ready-*"))) != len(processes):
            if any(child.poll() is not None for child in processes):
                raise RuntimeError("race writer exited before barrier")
            if time.monotonic() > deadline:
                raise TimeoutError("race barrier")
            time.sleep(0.01)
        (root / "start").write_text("go")
        outcomes = []
        for child in processes:
            out, err = child.communicate(timeout=30)
            assert child.returncode in (0, 3), (child.returncode, out, err)
            outcomes.append(child.returncode)
        assert outcomes.count(0) == 1 and outcomes.count(3) == len(processes) - 1, outcomes
        rows = json.loads(subprocess.check_output([*command, "read", str(root)], text=True, timeout=30))
        assert len(rows) == 1 and rows[0]["NewContent"] == "content", rows
        expected = ("Proposed",) if modes[0] == "race-add" else ("Approved", "Rejected")
        assert rows[0]["Status"] in expected, rows
        print(f"PASS cross-process {modes[0]}: one winner, {len(processes)-1} explicit refusals, one complete row", flush=True)
    finally:
        for child in processes:
            if child.poll() is None:
                child.kill()
                child.wait(timeout=10)



def run_probe(framework):
    with tempfile.TemporaryDirectory(prefix="proposal-crash-") as temporary:
        work = Path(temporary)
        links = "".join(f'<Compile Include="{escape(str(ROOT / "src/Ashlar.BackgroundAgents/Forge" / name))}" />' for name in SOURCES)
        (work / "Probe.csproj").write_text(f'''<Project Sdk="Microsoft.NET.Sdk">
<PropertyGroup><OutputType>Exe</OutputType><TargetFramework>{framework}</TargetFramework><ImplicitUsings>enable</ImplicitUsings>
<Nullable>enable</Nullable><EnableDefaultCompileItems>false</EnableDefaultCompileItems></PropertyGroup>
<ItemGroup><Compile Include="Program.cs" />{links}</ItemGroup></Project>''')
        (work / "Program.cs").write_text(PROGRAM)
        subprocess.run(["dotnet", "build", str(work / "Probe.csproj"), "--nologo", "-v", "minimal"], check=True, timeout=180)
        command = ["dotnet", str(work / f"bin/Debug/{framework}/Probe.dll")]
        stages = ("add.writing", "add.flushed", "add.published", "journal.writing", "journal.flushed",
                  "journal.published", "destination.writing", "destination.flushed", "destination.published",
                  "transition.destination-published", "transition.source-deleted", "transition.journal-deleted")
        for index, stage in enumerate(stages):
            root = work / f"store-{index}"
            root.mkdir()
            adding = stage.startswith("add.")
            if not adding:
                subprocess.run([*command, "add", str(root)], check=True, timeout=30)
            with subprocess.Popen([*command, "add" if adding else "approve", str(root), stage], stdout=subprocess.PIPE, stderr=subprocess.PIPE) as child:
                try:
                    deadline = time.monotonic() + 30
                    while not (root / "boundary.marker").exists():
                        if child.poll() is not None:
                            raise RuntimeError(f"writer exited before {stage}: {child.communicate()}")
                        if time.monotonic() >= deadline:
                            raise TimeoutError(stage)
                        time.sleep(0.01)
                    child.kill()
                    child.wait(timeout=10)
                    assert child.returncode < 0, (stage, child.returncode)
                finally:
                    if child.poll() is None:
                        child.kill()
                        child.wait(timeout=10)
            rows = json.loads(subprocess.check_output([*command, "read", str(root)], text=True, timeout=30))
            expected = None if stage in ("add.writing", "add.flushed") else (
                "Proposed" if stage in ("add.published", "journal.writing", "journal.flushed") else "Approved")
            assert len(rows) == (0 if expected is None else 1), (stage, rows)
            if expected is not None:
                assert rows[0]["Status"] == expected and rows[0]["NewContent"] == "content", (stage, rows)
                if expected == "Approved":
                    assert rows[0]["ResolvedBy"] == "alice" and rows[0]["ResolutionNote"] == "approved", rows
            again = json.loads(subprocess.check_output([*command, "read", str(root)], text=True, timeout=30))
            assert again == rows, (stage, "non-idempotent recovery")
            assert not list((root / ".transactions").glob("*.json")), stage
            print(f"PASS {framework} SIGKILL {stage}: {expected or 'absent'}; restart and second read consistent", flush=True)
        race_probe(command, work / "race-add", ["race-add"] * 12)
        race_probe(command, work / "race-decision", ["race-approve", "race-reject"] * 6)


if __name__ == "__main__":
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--framework", required=True, choices=["net8.0", "net10.0"])
    run_probe(parser.parse_args().framework)
