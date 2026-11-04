namespace OneChicago.Services;

/// <summary>
/// The copyable "recommended source" text shown in the Sources
/// section (Setup + Settings), next to the episode-links address
/// field.
///
/// ★ SAM — TO SET THE REAL SOURCE: replace the value of
/// RecommendedSourceText below with the address of a hosted
/// episode-links file (e.g. "https://…/onetimeline-prime-links.json")
/// when one exists. That is the whole change: the note renders this
/// text verbatim with a Copy button, so whatever stands here is
/// what users copy. Until then the value is a format example —
/// which is also the fallback that teaches the file shape if the
/// hosted address ever goes away.
/// </summary>
public static class SourcesNote
{
	public const string RecommendedSourceText =
		"{\n" +
		"  \"format\": \"onetimeline-prime-links\", \"version\": 1, \"universe\": \"one-chicago\",\n" +
		"  \"links\": { \"Fire|1|1\": \"amzn1.dv.gti.aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa\" }\n" +
		"}";
}
