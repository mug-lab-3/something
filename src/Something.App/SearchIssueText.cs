using Something.Application;

namespace Something.App;

/// <summary>Wording for the problems the conditions screen shows next to an input.</summary>
internal static class SearchIssueText
{
    public static string Describe(SearchFormIssue issue) =>
        issue.Problem switch
        {
            SearchFormProblem.RangeMissing => Strings.Get("Search_Issue_RangeMissing"),
            SearchFormProblem.NotANumber => Strings.Get("Search_Issue_NotANumber"),
            SearchFormProblem.NegativeNotAllowed => Strings.Get("Search_Issue_NegativeNotAllowed"),
            SearchFormProblem.TooLarge => Strings.Get("Search_Issue_TooLarge"),
            SearchFormProblem.Criteria => Describe(
                issue.Criteria ?? throw new ArgumentException("A criteria problem names its issue.")
            ),
            _ => throw new ArgumentOutOfRangeException(nameof(issue)),
        };

    private static string Describe(SearchCriteriaIssue issue) =>
        issue switch
        {
            SearchCriteriaIssue.RangeReversed => Strings.Get("Search_Issue_RangeReversed"),
            SearchCriteriaIssue.NoScansInRange => Strings.Get("Search_Issue_NoScansInRange"),
            SearchCriteriaIssue.ChangesNeedTwoScans => Strings.Get(
                "Search_Issue_ChangesNeedTwoScans"
            ),
            SearchCriteriaIssue.InvalidRegularExpression => Strings.Get(
                "Search_Issue_InvalidRegularExpression"
            ),
            SearchCriteriaIssue.InvalidExtension => Strings.Get("Search_Issue_InvalidExtension"),
            SearchCriteriaIssue.DepthRangeInvalid => Strings.Get("Search_Issue_DepthRangeInvalid"),
            SearchCriteriaIssue.SizeRangeReversed => Strings.Get("Search_Issue_SizeRangeReversed"),
            SearchCriteriaIssue.DeltaRangeReversed => Strings.Get(
                "Search_Issue_DeltaRangeReversed"
            ),
            SearchCriteriaIssue.RateRangeInvalid => Strings.Get("Search_Issue_RateRangeInvalid"),
            _ => throw new ArgumentOutOfRangeException(nameof(issue)),
        };
}
