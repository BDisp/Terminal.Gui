// CoPilot - GPT-6

namespace ApplicationTests;

/// <summary>
///     Serializes dispatch tests with other collections because application initialization can update
///     process-wide driver color settings while driver tests assert their instance settings.
/// </summary>
[CollectionDefinition ("Application Dispatch Tests", DisableParallelization = true)]
public class ApplicationDispatchTestCollection { }
