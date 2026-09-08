namespace Jiangyu.Loader.Templates;

/// <summary>
/// One mod's operations on one template, held because the template or a template they
/// refer to does not exist yet. Nothing in a held block has been written. It applies in
/// order, all at once, the pass its last missing template appears.
/// </summary>
internal sealed class HeldPatchBlock
{
    public HeldPatchBlock(
        string templateType,
        string templateId,
        string ownerLabel,
        IReadOnlyList<LoadedPatchOperation> ops,
        IReadOnlyList<TemplateRef> missing)
    {
        TemplateType = templateType;
        TemplateId = templateId;
        OwnerLabel = ownerLabel;
        Ops = ops;
        Missing = missing;
    }

    public string TemplateType { get; }
    public string TemplateId { get; }
    public string OwnerLabel { get; }
    public IReadOnlyList<LoadedPatchOperation> Ops { get; }
    /// <summary>The templates the block waits on, updated on each retry.</summary>
    public IReadOnlyList<TemplateRef> Missing { get; set; }
    public bool Reported { get; set; }
    /// <summary>A chained clone's block whose templates all exist: it belongs to the
    /// rebase-and-replay that follows the pass, which removes it once it has run. It stays
    /// held until then, so a rebuild that cannot run this pass is retried by the next.</summary>
    public bool ReleasedToReplay { get; set; }

    /// <summary>The block's template, keyed as <see cref="LateTemplateSet.Key"/>.</summary>
    public string TemplateKey => LateTemplateSet.Key(TemplateType, TemplateId);

    internal string Key => TemplateKey + "\0" + OwnerLabel;
}

/// <summary>The held blocks, one entry per mod and template. A template addressed under an
/// ancestor's name and under its own is one template, so one entry. Each is reported once.</summary>
internal sealed class HeldPatchBlocks
{
    private readonly Dictionary<string, HeldPatchBlock> _blocks = new(StringComparer.Ordinal);
    private readonly Func<string, string, bool> _sameSpace;

    public HeldPatchBlocks(Func<string, string, bool> sameSpace = null)
    {
        _sameSpace = sameSpace ?? TemplateRuntimeAccess.SameTemplateSpace;
    }

    public bool IsEmpty => _blocks.Count == 0;

    public int Count => _blocks.Count;

    /// <summary>Operations across every held block.</summary>
    public int PendingOps
    {
        get
        {
            var total = 0;
            foreach (var block in _blocks.Values)
                total += block.Ops.Count;
            return total;
        }
    }

    /// <summary>Adds the block. A block already held for the same mod and template, under
    /// any name of the template's type, keeps its first entry.</summary>
    public bool Add(HeldPatchBlock block)
    {
        if (Contains(block.TemplateType, block.TemplateId, block.OwnerLabel))
            return false;
        _blocks[block.Key] = block;
        return true;
    }

    public bool Remove(HeldPatchBlock block) => _blocks.Remove(block.Key);

    /// <summary>Removes the mod's block on the template, under any name of the template's
    /// type. Returns the block removed, or null.</summary>
    public HeldPatchBlock Remove(string ownerLabel, string templateType, string templateId)
    {
        foreach (var block in _blocks.Values)
        {
            if (string.Equals(block.OwnerLabel, ownerLabel, StringComparison.Ordinal)
                && string.Equals(block.TemplateId, templateId, StringComparison.Ordinal)
                && _sameSpace(templateType, block.TemplateType))
            {
                _blocks.Remove(block.Key);
                return block;
            }
        }

        return null;
    }

    public bool Contains(string templateType, string templateId, string ownerLabel)
        => Contains(ownerLabel, name => _sameSpace(templateType, name), templateId);

    /// <summary>Whether a mod's block on the template is held, the template addressed under
    /// any type <paramref name="typeMatches"/> accepts (its own, an ancestor, another
    /// spelling).</summary>
    public bool Contains(string ownerLabel, Func<string, bool> typeMatches, string templateId)
    {
        foreach (var block in _blocks.Values)
        {
            if (string.Equals(block.OwnerLabel, ownerLabel, StringComparison.Ordinal)
                && string.Equals(block.TemplateId, templateId, StringComparison.Ordinal)
                && typeMatches(block.TemplateType))
                return true;
        }

        return false;
    }

    /// <summary>Whether any mod's block on the template is held, the template addressed under
    /// any type <paramref name="typeMatches"/> accepts.</summary>
    public bool ContainsTemplate(Func<string, bool> typeMatches, string templateId)
    {
        foreach (var block in _blocks.Values)
        {
            if (string.Equals(block.TemplateId, templateId, StringComparison.Ordinal) && typeMatches(block.TemplateType))
                return true;
        }

        return false;
    }

    public List<HeldPatchBlock> Snapshot() => new(_blocks.Values);

    /// <summary>Whether a held block on the template (addressed under any type
    /// <paramref name="typeMatches"/> accepts) waits on a template <paramref name="isExcepted"/>
    /// does not accept. A clone waiting on its source's blocks asks this: a block that waits
    /// on that very clone is the one it does not wait for.</summary>
    public bool AnyBlockWaitsOnOther(Func<string, bool> typeMatches, string templateId, Func<TemplateRef, bool> isExcepted)
    {
        foreach (var block in _blocks.Values)
        {
            if (!string.Equals(block.TemplateId, templateId, StringComparison.Ordinal) || !typeMatches(block.TemplateType))
                continue;
            foreach (var missing in block.Missing)
            {
                if (!isExcepted(missing))
                    return true;
            }
        }

        return false;
    }

    /// <summary>Marks every block unreported, so the next schedule end reports it again.</summary>
    public void ResetReported()
    {
        foreach (var block in _blocks.Values)
            block.Reported = false;
    }

    /// <summary>Blocks not yet reported, each marked reported as it is returned.</summary>
    public List<HeldPatchBlock> TakeUnreported()
    {
        var unreported = new List<HeldPatchBlock>();
        foreach (var block in _blocks.Values)
        {
            if (block.Reported || block.ReleasedToReplay)
                continue;
            block.Reported = true;
            unreported.Add(block);
        }

        return unreported;
    }
}
