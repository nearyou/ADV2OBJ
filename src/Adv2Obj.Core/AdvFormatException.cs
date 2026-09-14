namespace Adv2Obj.Core;

public sealed class AdvFormatException(string message, Exception? innerException = null)
    : Exception(message, innerException);
