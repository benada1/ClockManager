namespace ClockManager.Api.Domain;

/// <summary>Stored as text (<c>'Employee'</c> / <c>'Manager'</c>) and restricted by <c>CK_Employees_Role</c>.</summary>
public enum EmployeeRole
{
    Employee,
    Manager,
}
