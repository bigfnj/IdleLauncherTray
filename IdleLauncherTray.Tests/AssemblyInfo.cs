using Xunit;

// Several of these tests move process-global state: the IDLELAUNCHERTRAY_DATA_DIR
// override, the current working directory, and the files inside the redirected data
// directory. xunit runs test classes in parallel by default, which would let one test's
// environment change leak into another's arrangement and produce failures that depend on
// scheduling. The whole suite runs in well under a second, so serialising it costs
// nothing and removes an entire class of false red.
[assembly: CollectionBehavior(DisableTestParallelization = true)]
