using System.ComponentModel.DataAnnotations;

namespace CoursePilot.Host.Modules.Users.Transports.Http;

public sealed class PostSearchModel : IValidatableObject
{
    [Range(0, int.MaxValue)]
    public int Offset { get; set; }
    [Range(1, 100)]
    public int Limit { get; set; } = 25;
    public string? Role { get; set; }
    public string? Approval { get; set; }
    public IEnumerable<ValidationResult> Validate(ValidationContext validationContext)
    {
        if (Limit > 0 && (Offset > int.MaxValue - Limit))
            yield return new ValidationResult("Offset and limit exceed the supported range.", [nameof(Offset)]);
    }
}
