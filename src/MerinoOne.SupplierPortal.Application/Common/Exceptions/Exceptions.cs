namespace MerinoOne.SupplierPortal.Application.Common.Exceptions;

public class NotFoundException : Exception
{
    public NotFoundException(string entity, object key) : base($"{entity} '{key}' not found.") { }
}

public class ForbiddenException : Exception
{
    public ForbiddenException(string message = "Access denied.") : base(message) { }
}

public class ValidationException : Exception
{
    public IReadOnlyDictionary<string, string[]> Errors { get; }
    public ValidationException(IReadOnlyDictionary<string, string[]> errors) : base("Validation failed.")
    {
        Errors = errors;
    }
}

public class ConflictException : Exception
{
    public ConflictException(string message) : base(message) { }
}
