using System;
using System.Collections.Generic;
using UnityEngine;

namespace UniSpring.DependencyInjection
{
    /// <summary>
    /// A bean that runs between the container's start and its end, as Spring's <c>Lifecycle</c>. Application beans
    /// and registered instances that implement it are started once every application bean exists (in bean order,
    /// see <see cref="OrderAttribute"/>) and stopped in reverse order before the container disposes its beans.
    /// A <see cref="Start"/> that throws fails the build; a <see cref="Stop"/> that throws does not stop the others.
    /// </summary>
    public interface ILifecycle
    {
        void Start();

        void Stop();
    }

    /// <summary>
    /// Registers the bean only while one of the profiles is active, as Spring's <c>@Profile</c>. A name with a
    /// leading <c>!</c> matches when that profile is not active. The default profiles are <c>Editor</c> or
    /// <c>Player</c>, plus <c>Development</c> in a development build, plus the comma-separated names of the
    /// <c>UNISPRING_PROFILES</c> environment variable (see <see cref="ContextOptions.Profiles"/>).
    /// </summary>
    [AttributeUsage(AttributeTargets.Class, Inherited = false)]
    public sealed class ProfileAttribute : Attribute
    {
        public ProfileAttribute(params string[] profiles)
        {
            Profiles = profiles ?? Array.Empty<string>();
        }

        public string[] Profiles { get; }

        internal bool IsActive(ICollection<string> active)
        {
            for (var index = 0; index < Profiles.Length; index++)
            {
                var name = Profiles[index];
                if (string.IsNullOrEmpty(name))
                    continue;
                if (name[0] == '!')
                {
                    if (!active.Contains(name.Substring(1)))
                        return true;
                    continue;
                }

                if (active.Contains(name))
                    return true;
            }

            return false;
        }
    }

    /// <summary>How a container is built; every property is optional.</summary>
    public sealed class ContextOptions
    {
        public const string EnvironmentVariable = "UNISPRING_PROFILES";

        /// <summary>The active profiles; null means <see cref="DefaultProfiles"/>.</summary>
        public ISet<string> Profiles { get; set; }

        /// <summary>The optional module-boundary policy; null keeps it disabled.</summary>
        public ModuleBoundaryOptions Boundaries { get; set; }

        /// <summary>Composition hook: register instances before any bean is created.</summary>
        public Action<ApplicationContext> Configure { get; set; }

        /// <summary><c>Editor</c> or <c>Player</c>, <c>Development</c> in a development build, and the names in <c>UNISPRING_PROFILES</c>.</summary>
        public static ISet<string> DefaultProfiles()
        {
            var profiles = new HashSet<string>(StringComparer.Ordinal)
            {
                Application.isEditor ? "Editor" : "Player"
            };
            if (Debug.isDebugBuild)
                profiles.Add("Development");
            var extra = Environment.GetEnvironmentVariable(EnvironmentVariable);
            if (!string.IsNullOrEmpty(extra))
            {
                foreach (var name in extra.Split(','))
                {
                    var trimmed = name.Trim();
                    if (trimmed.Length > 0)
                        profiles.Add(trimmed);
                }
            }

            return profiles;
        }
    }
}
