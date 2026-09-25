using AppPlatform.Ids;
using AppPlatform.Tenancy;

namespace AppPlatform.Audit;

/// <summary>
/// Opts an entity into automatic audit. Its creates, updates and deletes each produce an
/// <see cref="AuditEntry"/> in the same save.
///
/// Requires a public id so the row names the entity the way everything outside the database does,
/// and still means something after the entity is deleted. Requires tenant scope because an audit
/// row belongs to the tenant whose data changed — a row with no tenant could not be filtered, and
/// the insert guard would refuse it anyway.
/// </summary>
public interface IAuditable : IPublicIdentified, ITenantScoped;

/// <summary>
/// The audit row records THAT this property changed, never its value. For secrets (a password
/// hash) and for anything encrypted at rest — copying its plaintext into the audit log would undo
/// the encryption.
/// </summary>
[AttributeUsage(AttributeTargets.Property)]
public sealed class AuditRedactedAttribute : Attribute;
