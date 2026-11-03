using System;
using System.Collections.Generic;

namespace Amiss.Shell.Simulation
{
    /// <summary>
    /// Exactly one scientific writer per Trial, bound before the first Tick.
    /// Re-binding the same writer instance is idempotent; binding a different
    /// writer for the same Trial is refused, so no two engines can advance one
    /// Trial's ball state. A writer disposition of
    /// <see cref="WriterDisposition.ArchivedOnly"/> is refused here, at the one
    /// binding point, so a rejected configuration can never reach a product
    /// Trial through any factory or harness.
    /// </summary>
    public sealed class ScientificWriterSlot
    {
        readonly Dictionary<string, WriterBinding> bindings = new(StringComparer.Ordinal);

        public bool TryBind(string trialId, IScientificWriter writer,
            string configurationId, out string reason)
        {
            reason = "";
            if (string.IsNullOrWhiteSpace(trialId) || writer == null)
            {
                reason = "writer_binding_invalid";
                return false;
            }
            if (writer.Disposition != WriterDisposition.ProductTrial)
            {
                reason = "writer_configuration_archived_only";
                return false;
            }
            if (bindings.TryGetValue(trialId, out var existing))
            {
                if (!ReferenceEquals(existing.Writer, writer))
                {
                    reason = "writer_already_bound";
                    return false;
                }
                return true;
            }
            bindings.Add(trialId, new WriterBinding(trialId, writer, configurationId));
            return true;
        }

        public bool IsBound(string trialId) =>
            !string.IsNullOrEmpty(trialId) && bindings.ContainsKey(trialId);

        public IScientificWriter WriterFor(string trialId) =>
            bindings.TryGetValue(trialId ?? "", out var binding) ? binding.Writer : null;

        public WriterBinding Binding(string trialId) =>
            bindings.TryGetValue(trialId ?? "", out var binding) ? binding : null;

        public int BoundWriterCount => bindings.Count;

        public void Release(string trialId)
        {
            if (!string.IsNullOrEmpty(trialId))
                bindings.Remove(trialId);
        }
    }

    public sealed class WriterBinding
    {
        public WriterBinding(string trialId, IScientificWriter writer, string configurationId)
        {
            TrialId = trialId ?? "";
            Writer = writer;
            ConfigurationId = configurationId ?? "";
        }

        public string TrialId { get; }
        public IScientificWriter Writer { get; }
        public string ConfigurationId { get; }
        public string WriterId => Writer?.WriterId ?? "";
    }
}
