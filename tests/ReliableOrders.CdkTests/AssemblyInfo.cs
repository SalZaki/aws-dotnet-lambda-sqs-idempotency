// Every CDK call in this assembly crosses into one node process through jsii, over a single pipe
// shared by the whole test host. xunit runs test classes in parallel by default, and the moment a
// second class started synthesising, results came back belonging to another test — 22 failures in a
// suite where every case passes when its class is run alone, and a run that then hung rather than
// finishing.
//
// Disabled at the assembly level rather than by grouping the CDK cases into one collection, because
// the constraint is the runtime's and applies to any class added later. Synthesis is a few seconds,
// so the cost is a suite that stays under a minute either way.
[assembly: CollectionBehavior(DisableTestParallelization = true)]
