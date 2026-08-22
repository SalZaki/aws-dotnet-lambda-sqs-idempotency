using Xunit.Sdk;
using Xunit.v3;

// Every CDK call in this assembly crosses into one node process through jsii, over a single pipe
// shared by the whole test host. xunit runs test classes in parallel by default, and the moment a
// second class started synthesising, results came back belonging to another test — 22 failures in a
// suite where every case passes when its class is run alone, and a run that then hung rather than
// finishing.
//
// Disabled at the assembly level rather than by grouping the CDK cases into one collection, because
// the constraint is the runtime's and applies to any class added later. Synthesis is a few seconds,
// so the cost is a suite that stays under a minute either way.
//
// ParallelMode.None rather than the CollectionBehavior property this replaces: xunit 4 removed it,
// and None is the value that disables all parallelism rather than only parallelism between
// collections, which is what the pipe can survive.
[assembly: Parallelization(Mode = ParallelMode.None)]
