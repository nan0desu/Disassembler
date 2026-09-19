using Disassembler.CPU;
using System.Text;

namespace Disassembler
{
	public class ILVariable : ILExpression
	{
		private ProgramFunction? parent;
		private string? name = null;
		private ILVariableScopeEnum scope = ILVariableScopeEnum.LocalVariable;
		private ILValueType valueType = ILValueType.Void;
		private int arraySize = 0;
		private int offset = 0;
		private int valueTypeConfidence = 0;
		private object? initialValue = null;
		private bool variableArguments = false;

		public ILVariable(ProgramFunction? parent, ILValueType type, int offset) : this(parent, ILVariableScopeEnum.LocalVariable, type, offset, 0) { }

		public ILVariable(ILVariableScopeEnum scope, ILValueType type, int offset, int valueTypeConfidence) : this(null, null, scope, type, offset, valueTypeConfidence) { }

		public ILVariable(ProgramFunction? parent, ILVariableScopeEnum scope, ILValueType type, int offset) : this(parent, null, scope, type, offset, 0) { }

		public ILVariable(string name, ILVariableScopeEnum scope, ILValueType type, int offset, int valueTypeConfidence) : this(null, name, scope, type, offset, valueTypeConfidence) { }

		public ILVariable(string name, ILVariableScopeEnum scope, ILValueType type, int offset, int valueTypeConfidence, bool variableArguments) : 
			this(null, name, scope, type, offset, valueTypeConfidence, variableArguments) { }

		public ILVariable(ProgramFunction? parent, ILVariableScopeEnum scope, ILValueType type, int offset, int valueTypeConfidence) : this(parent, null, scope, type, offset, valueTypeConfidence) { }

		public ILVariable(ProgramFunction? parent, string? name, ILVariableScopeEnum scope, ILValueType valueType, int offset, int valueTypeConfidence) :
			this(parent, name, scope, valueType, offset, valueTypeConfidence, false) { }

		public ILVariable(ProgramFunction? parent, string? name, ILVariableScopeEnum scope, ILValueType valueType, int offset, int valueTypeConfidence, bool variableArguments)
		{
			this.parent = parent;
			this.name = name;
			this.scope = scope;
			this.valueType = valueType;
			this.offset = offset;
			this.valueTypeConfidence = valueTypeConfidence;
			this.variableArguments = variableArguments;
		}

		public string CSDeclaration
		{
			get
			{
				switch (this.scope)
				{
					case ILVariableScopeEnum.Global:
						return $"public {this.valueType.CSDeclaration}{((this.arraySize > 0) ? $"[]" : "")} {this.Name}{this.CSInitialization}";

					case ILVariableScopeEnum.LocalParameter:
						return $"{this.valueType.CSDeclaration}{((this.arraySize > 0) ? $"[]" : "")} {this.Name}";

					case ILVariableScopeEnum.LocalVariable:
						return $"{this.valueType.CSDeclaration}{((this.arraySize > 0) ? $"[]" : "")} {this.Name}{this.CSInitialization}";

					default:
						return $"object {this.Name}";
				}
			}
		}

		public string CSInitialization
		{
			get
			{
				if (this.arraySize > 0)
				{
					switch (this.valueType.BaseType)
					{
						case ILBaseValueTypeEnum.UInt8:
							return $" = new byte{((this.arraySize > 0) ? $"[{this.arraySize}]" : "")}";

						case ILBaseValueTypeEnum.Int8:
							return $" = new sbyte{((this.arraySize > 0) ? $"[{this.arraySize}]" : "")}";

						case ILBaseValueTypeEnum.UInt16:
							return $" = new ushort{((this.arraySize > 0) ? $"[{this.arraySize}]" : "")}";

						case ILBaseValueTypeEnum.Int16:
							return $" = new short{((this.arraySize > 0) ? $"[{this.arraySize}]" : "")}";

						case ILBaseValueTypeEnum.UInt32:
							return $" = new uint{((this.arraySize > 0) ? $"[{this.arraySize}]" : "")}";

						case ILBaseValueTypeEnum.Int32:
							return $" = new int{((this.arraySize > 0) ? $"[{this.arraySize}]" : "")}";

						case ILBaseValueTypeEnum.Ptr16:
							return $" = new {this.valueType.ReferencedType.CSDeclaration} *{((this.arraySize > 0) ? $"[{this.arraySize}]" : "")}";

						case ILBaseValueTypeEnum.Ptr32:
							return $" = new {this.valueType.ReferencedType.CSDeclaration} *{((this.arraySize > 0) ? $"[{this.arraySize}]" : "")}";

						case ILBaseValueTypeEnum.FnPtr32:
							return $" = new {this.valueType.ReferencedType.CSDeclaration} *{((this.arraySize > 0) ? $"[{this.arraySize}]" : "")}";

						default:
							return $" = new object{((this.arraySize > 0) ? "[]" : "")}";
					}
				}

				return "";
			}
		}

		override public string ToCSString()
		{
			return this.Name;
		}

		public override string ToString()
		{
			return this.Name;
		}

		public ProgramFunction? Parent { get => this.parent; }

		private static readonly HashSet<string> CSharpKeywords =
		[
			"abstract", "as", "base", "bool", "break", "byte", "case", "catch", "char", "checked",
			"class", "const", "continue", "decimal", "default", "delegate", "do", "double", "else",
			"enum", "event", "explicit", "extern", "false", "finally", "fixed", "float", "for",
			"foreach", "goto", "if", "implicit", "in", "int", "interface", "internal", "is", "lock",
			"long", "namespace", "new", "null", "object", "operator", "out", "override", "params",
			"private", "protected", "public", "readonly", "ref", "return", "sbyte", "sealed", "short",
			"sizeof", "stackalloc", "static", "string", "struct", "switch", "this", "throw", "true",
			"try", "typeof", "uint", "ulong", "unchecked", "unsafe", "ushort", "using", "virtual",
			"void", "volatile", "while"
		];

		public string Name
		{
			get
			{
				if (this.name != null)
				{
					if (CSharpKeywords.Contains(this.name))
						return $"@{this.name}";

					return this.name;
				}
				else
				{
					switch (this.scope)
					{
						case ILVariableScopeEnum.LocalVariable:
							return $"Local_{this.Offset:x}";

						case ILVariableScopeEnum.LocalParameter:
							return $"Param_{this.Offset:x}";

						case ILVariableScopeEnum.Global:
							return $"Global_{this.Offset:x}";

						default:
							throw new Exception($"Undefined variable scope {this.scope}");
					}
				}
			}
			set
			{
				if (string.IsNullOrEmpty(value))
				{
					this.name = null;
				}
				else
				{
					this.name = value;
				}
			}
		}

		public ILVariableScopeEnum Scope { get => this.scope; set => this.scope = value; }

		public ILValueType ValueType { get => this.valueType; set => this.valueType = value; }

		public int ArraySize { get => this.arraySize; set => this.arraySize = value; }

		public int ValueTypeConfidence { get => this.valueTypeConfidence; set => this.valueTypeConfidence = Math.Min(Math.Max(value, 0), 10); }

		public int Offset { get => this.offset; set => this.offset = value; }

		public object? InitialValue { get => this.initialValue; set => this.initialValue = value; }

		public bool VariableArguments { get => this.variableArguments;  set => this.variableArguments = value; }
	}
}
