namespace MaddoxTasks.Application;

public sealed class CommandValidationException : Exception
{
    public CommandValidationException(string message) : base(message)
    {
    }
}

