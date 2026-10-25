using System;

namespace LalabAutoReport.Core.Domain;

public class BillClaimConflictException : Exception
{
    public BillClaimConflictException(string message) : base(message)
    {
    }

    public BillClaimConflictException(string message, Exception innerException) : base(message, innerException)
    {
    }
}
