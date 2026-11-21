using Paldium.Core.Domain;

namespace Paldium.Core.Breeding;

/// <summary>
/// One way to obtain a child species. <see cref="AReq"/> / <see cref="BReq"/> are <see cref="GenderReq.Any"/>
/// except for the few recipes that depend on the parents' genders.
/// </summary>
public sealed record ParentPair(string A, string B, GenderReq AReq, GenderReq BReq);

/// <summary>One possible result of breeding two species; the genders say which parent genders produce it.</summary>
public sealed record ChildOutcome(string ChildId, GenderReq AGender, GenderReq BGender);
