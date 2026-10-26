namespace MCP.Core.Attributes
{
    [AttributeUsage(AttributeTargets.Class, AllowMultiple = false, Inherited = false)]
    public class ControllerAttribute : Attribute
    {
        public string Name { get; private set; }

        public ControllerAttribute(string name)
        {
            if (string.IsNullOrWhiteSpace(name) == true)
            {
                throw new ArgumentException("An MCP controller name is required.", nameof(name));
            }
            Name = name;
        }
    }
}
