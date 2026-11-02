// LocalizationService is process-wide; tests that switch the language must not
// overlap with tests asserting English text.
[assembly: Xunit.CollectionBehavior(DisableTestParallelization = true)]
