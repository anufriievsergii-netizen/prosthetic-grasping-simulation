using System;
using System.Collections.Generic;
using UnityEngine.Perception.Randomization.Parameters;
using UnityEngine.Perception.Randomization.Randomizers.SampleRandomizers.Tags;
using UnityEngine.Perception.Randomization.Samplers;

namespace UnityEngine.Perception.Randomization.Randomizers.SampleRandomizers
{
    /// <summary>
    /// Randomizes the material of table tagged with a TableMaterialRandomizerTag
    /// </summary>
    [Serializable]
    [AddRandomizerMenu("Perception/Table Material Randomizer")]
    public class TableMaterialRandomizer : Randomizer
    {
        /// <summary>
        /// The list of materials to sample and apply to target table
        /// </summary>
        [Tooltip("The list of materials to sample and apply to target table.")]
        public CategoricalParameter<Material> materials;

        /// The sampler to get a sample float in range [0, 1], this will be used
        /// to decide whether to apply a random material to the object or not
        private UniformSampler samplerApplyMaterial;
        /// A float value between 0 and 1. The higher the value, the more likely it is
        /// that a material will be applied to the table
        public float samplingRangeMaterial;
        // The renderers whose material was replaced in this iteration, with their original
        // material, such that it is restored at the end of the iteration
        private readonly List<(Renderer renderer, Material original)> replaced = new();

        protected override void OnAwake()
        {
            samplerApplyMaterial = new UniformSampler(0, 1);
        }

        /// <summary>
        /// Randomizes the material of tagged table at the start of each scenario iteration
        /// The randomization is performed only if the sampled float value
        /// falls within the user-defined sampling range.
        /// Works on sharedMaterial: reading Renderer.material would create a new copy of the
        /// material asset in the slot, and those copies are never destroyed
        /// </summary>
        protected override void OnIterationStart()
        {
            var tags = tagManager.Query<TableMaterialRandomizerTag>();
            foreach (var tag in tags)
            {
                float sampledFloat = samplerApplyMaterial.Sample();
                if (sampledFloat <= samplingRangeMaterial)
                {
                    Renderer renderer = tag.GetComponent<Renderer>();
                    replaced.Add((renderer, renderer.sharedMaterial));
                    renderer.sharedMaterial = materials.Sample();
                }
            }
        }

        /// <summary>
        /// Restores the original material of every table changed in this iteration
        /// </summary>
        protected override void OnIterationEnd()
        {
            foreach (var (renderer, original) in replaced)
                if (renderer != null)
                    renderer.sharedMaterial = original;
            replaced.Clear();
        }
    }
}
