using Xunit;

// The caption model is a process-wide singleton and the caption pipeline tests drive its real
// translation/display loops. Running test classes concurrently against that state was a latent
// flake waiting for the first test that needed more than one call.
[assembly: CollectionBehavior(DisableTestParallelization = true)]
