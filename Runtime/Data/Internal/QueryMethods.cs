using System;
using System.Collections.Generic;
using System.Reflection;

namespace UniSpring.Data.Internal
{
    /// <summary>
    /// Query methods a repository interface declares by name: a verb (<c>FindBy</c>, <c>CountBy</c>,
    /// <c>ExistsBy</c>, <c>DeleteBy</c>) followed by member names of the stored type joined by <c>And</c>, one
    /// parameter per member. Every stored object is loaded and compared in memory.
    /// </summary>
    internal static class QueryMethods
    {
        private sealed class Member
        {
            public string Name;
            public Type Type;
            public Func<object, object> Get;
        }

        private delegate object Shape(Type domain, List<KeyValuePair<string, object>> matches, Action<string> delete);

        private static readonly KeyValuePair<string, Func<MethodInfo, Type, Shape>>[] Verbs =
        {
            new KeyValuePair<string, Func<MethodInfo, Type, Shape>>("FindBy", Find),
            new KeyValuePair<string, Func<MethodInfo, Type, Shape>>("CountBy", Count),
            new KeyValuePair<string, Func<MethodInfo, Type, Shape>>("ExistsBy", Exists),
            new KeyValuePair<string, Func<MethodInfo, Type, Shape>>("DeleteBy", Delete),
        };

        public static Func<object[], object> Build(MethodInfo method, DocumentAccess access, string area)
        {
            var domain = access.DomainType;
            for (var index = 0; index < Verbs.Length; index++)
            {
                if (!method.Name.StartsWith(Verbs[index].Key, StringComparison.Ordinal))
                    continue;

                var members = ResolveMembers(method, method.Name.Substring(Verbs[index].Key.Length), domain);
                RequireParameters(method, members);
                var shape = Verbs[index].Value(method, domain);
                return arguments => shape(domain, Matches(access, area, members, arguments), id => access.Delete(area, id));
            }

            throw Refused(method, "its name must start with FindBy, CountBy, ExistsBy or DeleteBy");
        }

        private static List<KeyValuePair<string, object>> Matches(
            DocumentAccess access, string area, List<Member> members, object[] arguments)
        {
            var result = new List<KeyValuePair<string, object>>();
            var ids = access.Names(area);
            for (var index = 0; index < ids.Count; index++)
            {
                var value = access.Load(area, ids[index]);
                if (value != null && IsMatch(value, members, arguments))
                    result.Add(new KeyValuePair<string, object>(ids[index], value));
            }

            return result;
        }

        private static bool IsMatch(object value, List<Member> members, object[] arguments)
        {
            for (var index = 0; index < members.Count; index++)
            {
                if (!Equals(members[index].Get(value), arguments[index]))
                    return false;
            }

            return true;
        }

        private static Shape Find(MethodInfo method, Type domain)
        {
            if (method.ReturnType == domain)
                return (type, matches, delete) => matches.Count > 0 ? matches[0].Value : null;
            if (method.ReturnType.IsAssignableFrom(domain.MakeArrayType()))
                return (type, matches, delete) => ToArray(type, matches);
            throw Refused(method, "a FindBy method returns " + domain.Name + " (the first match or null) or a " +
                                  "read-only list of it");
        }

        private static Shape Count(MethodInfo method, Type domain)
        {
            if (method.ReturnType != typeof(int))
                throw Refused(method, "a CountBy method returns int");
            return (type, matches, delete) => matches.Count;
        }

        private static Shape Exists(MethodInfo method, Type domain)
        {
            if (method.ReturnType != typeof(bool))
                throw Refused(method, "an ExistsBy method returns bool");
            return (type, matches, delete) => matches.Count > 0;
        }

        private static Shape Delete(MethodInfo method, Type domain)
        {
            var counts = method.ReturnType == typeof(int);
            if (!counts && method.ReturnType != typeof(void))
                throw Refused(method, "a DeleteBy method returns void or int (the number deleted)");
            return (type, matches, delete) =>
            {
                for (var index = 0; index < matches.Count; index++)
                    delete(matches[index].Key);
                return counts ? (object)matches.Count : null;
            };
        }

        private static Array ToArray(Type domain, List<KeyValuePair<string, object>> matches)
        {
            var array = Array.CreateInstance(domain, matches.Count);
            for (var index = 0; index < matches.Count; index++)
                array.SetValue(matches[index].Value, index);
            return array;
        }

        private static void RequireParameters(MethodInfo method, List<Member> members)
        {
            var parameters = method.GetParameters();
            if (parameters.Length != members.Count)
                throw Refused(method, "it names " + members.Count + " member(s) but takes " + parameters.Length +
                                      " parameter(s)");
            for (var index = 0; index < parameters.Length; index++)
            {
                if (!members[index].Type.IsAssignableFrom(parameters[index].ParameterType))
                    throw Refused(method, "parameter '" + parameters[index].Name + "' is " +
                                          parameters[index].ParameterType.Name + " but " + members[index].Name +
                                          " is " + members[index].Type.Name);
            }
        }

        /// <summary>
        /// Splits "OwnerAndModel" into members. A member name may itself contain "And" (BrandAndSeries), so the
        /// split is found by trying the members, not by cutting at every "And".
        /// </summary>
        private static List<Member> ResolveMembers(MethodInfo method, string names, Type domain)
        {
            var result = new List<Member>();
            if (names.Length == 0 || !Resolve(names, MembersOf(domain), result))
                throw Refused(method, "'" + names + "' is not a list of public fields or properties of " +
                                      domain.Name + " joined by And");
            return result;
        }

        private static bool Resolve(string rest, List<Member> members, List<Member> result)
        {
            const string joiner = "And";
            for (var index = 0; index < members.Count; index++)
            {
                var name = members[index].Name;
                if (!rest.StartsWith(name, StringComparison.Ordinal))
                    continue;

                result.Add(members[index]);
                if (rest.Length == name.Length)
                    return true;
                if (string.CompareOrdinal(rest, name.Length, joiner, 0, joiner.Length) == 0 &&
                    Resolve(rest.Substring(name.Length + joiner.Length), members, result))
                {
                    return true;
                }

                result.RemoveAt(result.Count - 1);
            }

            return false;
        }

        private static List<Member> MembersOf(Type domain)
        {
            var members = new List<Member>();
            const BindingFlags flags = BindingFlags.Instance | BindingFlags.Public;
            foreach (var field in domain.GetFields(flags))
            {
                var captured = field;
                members.Add(new Member { Name = field.Name, Type = field.FieldType, Get = captured.GetValue });
            }

            foreach (var property in domain.GetProperties(flags))
            {
                if (!property.CanRead || property.GetIndexParameters().Length != 0)
                    continue;
                var captured = property;
                members.Add(new Member { Name = property.Name, Type = property.PropertyType, Get = target => captured.GetValue(target, null) });
            }

            return members;
        }

        private static InvalidOperationException Refused(MethodInfo method, string reason)
            => new InvalidOperationException(
                "UniSpring Data cannot implement " + method.DeclaringType?.FullName + "." + method.Name + ": " +
                reason + ".");
    }
}
