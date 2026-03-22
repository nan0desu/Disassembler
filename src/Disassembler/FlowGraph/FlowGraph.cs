using Disassembler.CPU;
using IRB.Collections.Generic;
using System.Text;

namespace Disassembler
{
	public class FlowGraph
	{
		private ProgramFunction parentFunction;
		private string name = "";

		private FlowGraphNode? startNode = null;
		private BDictionary<uint, FlowGraphNode> endNodes = [];
		private BDictionary<uint, FlowGraphNode> nodes = [];
		private bool hasBPFrame = false;
		private bool usesSI = false;
		private bool usesDI = false;
		private bool usesDS = false;

		private FlowGraphLocalEnum requiredLocals = FlowGraphLocalEnum.None;
		// the compiler has these defined on the entry to the function
		private FlowGraphLocalEnum definedLocals = FlowGraphLocalEnum.CS | FlowGraphLocalEnum.DS | FlowGraphLocalEnum.SS | FlowGraphLocalEnum.SP;

		public FlowGraph(ProgramFunction fn)
		{
			this.parentFunction = fn;
		}

		public FlowGraph(ProgramFunction fn, string name)
		{
			this.parentFunction = fn;
			this.name = name;

			if (!this.parentFunction.IsLibraryFunction)
			{
				ConstructGraph();
				DetermineFunctionBPFrame();
				ConstructLocalRequirements();
			}
		}

		private void ConstructGraph()
		{
			Queue<FlowGraphNode> unprocessedNodes = new();

			this.startNode = null;
			this.endNodes.Clear();
			this.nodes.Clear();
			this.requiredLocals = FlowGraphLocalEnum.None;
			this.definedLocals = FlowGraphLocalEnum.None;

			int instructionCount = this.parentFunction.AsmInstructions.Count;
			int instructionPos = this.parentFunction.GetInstructionPositionByLinearAddress(this.parentFunction.FunctionEntryPoint);

			if (instructionPos < 0)
			{
				throw new Exception($"Expected instruction at 0x{this.parentFunction.FunctionEntryPoint:x}");
			}

			this.startNode = CreateOrFindNode(0, FlowGraphNodeTypeEnum.Start, unprocessedNodes, false);

			FlowGraphNode currentNode = CreateOrFindNode(this.parentFunction.FunctionEntryPoint, FlowGraphNodeTypeEnum.Block, unprocessedNodes, false);

			this.startNode.ChildNodes.Add(currentNode);

			while (instructionPos >= 0 && instructionPos < instructionCount)
			{
				CPUInstruction instruction = this.parentFunction.AsmInstructions[instructionPos];
				CPUParameter parameter;
				FlowGraphNode newNode;
				bool blockEnd = false;

				if (instruction.Label)
				{
					if (!this.nodes.ContainsKey(instruction.LinearAddress))
					{
						newNode = CreateOrFindNode(instruction.LinearAddress, FlowGraphNodeTypeEnum.Block, unprocessedNodes, true);
						currentNode.ChildNodes.Add(newNode);

						blockEnd = true;
					}
					else if (currentNode.LinearAddress != instruction.LinearAddress)
					{
						currentNode.ChildNodes.Add(this.nodes.GetValueByKey(instruction.LinearAddress));

						blockEnd = true;
					}
				}

				if (!blockEnd)
				{
					switch (instruction.InstructionType)
					{
						// Arithmetic instructions
						case CPUInstructionEnum.ADC:
						case CPUInstructionEnum.SBB:
							currentNode.AsmInstructions.Add(instruction);
							break;

						case CPUInstructionEnum.ADD:
						case CPUInstructionEnum.SUB:
						case CPUInstructionEnum.AND:
						case CPUInstructionEnum.OR:
						case CPUInstructionEnum.XOR:
							currentNode.AsmInstructions.Add(instruction);
							break;

						case CPUInstructionEnum.DEC:
						case CPUInstructionEnum.INC:
						case CPUInstructionEnum.NEG:
						case CPUInstructionEnum.NOT:
							currentNode.AsmInstructions.Add(instruction);
							break;

						case CPUInstructionEnum.DAS:
							currentNode.AsmInstructions.Add(instruction);
							break;

						// Multiply and divide instructions
						case CPUInstructionEnum.MUL:
							currentNode.AsmInstructions.Add(instruction);
							break;

						case CPUInstructionEnum.IMUL:
							currentNode.AsmInstructions.Add(instruction);
							break;

						case CPUInstructionEnum.DIV:
						case CPUInstructionEnum.IDIV:
							currentNode.AsmInstructions.Add(instruction);
							break;

						// Shifting instructions
						case CPUInstructionEnum.SAR:
						case CPUInstructionEnum.SHL:
						case CPUInstructionEnum.SHR:
						case CPUInstructionEnum.RCR:
						case CPUInstructionEnum.RCL:
						case CPUInstructionEnum.ROL:
						case CPUInstructionEnum.ROR:
							currentNode.AsmInstructions.Add(instruction);
							break;

						// Converting instructions
						case CPUInstructionEnum.CBW:
							currentNode.AsmInstructions.Add(instruction);
							break;

						case CPUInstructionEnum.CWD:
							currentNode.AsmInstructions.Add(instruction);
							break;

						// Compare and test instructions
						case CPUInstructionEnum.CMP:
						case CPUInstructionEnum.TEST:
							currentNode.AsmInstructions.Add(instruction);
							break;

						// Load segment and offset instructions
						case CPUInstructionEnum.LDS:
							currentNode.AsmInstructions.Add(instruction);
							break;

						case CPUInstructionEnum.LES:
							currentNode.AsmInstructions.Add(instruction);
							break;

						case CPUInstructionEnum.LEA:
							currentNode.AsmInstructions.Add(instruction);
							break;

						// String instructions
						case CPUInstructionEnum.LODS:
							currentNode.AsmInstructions.Add(instruction);
							break;

						case CPUInstructionEnum.MOVS:
							currentNode.AsmInstructions.Add(instruction);
							break;

						case CPUInstructionEnum.OUTS:
							currentNode.AsmInstructions.Add(instruction);
							break;

						case CPUInstructionEnum.STOS:
							currentNode.AsmInstructions.Add(instruction);
							break;

						// Data move and convert instructions
						case CPUInstructionEnum.MOV:
							currentNode.AsmInstructions.Add(instruction);
							break;

						// Ignored instructions
						case CPUInstructionEnum.WAIT:
						case CPUInstructionEnum.NOP:
							break;

						// BP based stack frame instructions
						case CPUInstructionEnum.ENTER:
							currentNode.AsmInstructions.Add(instruction);
							break;

						case CPUInstructionEnum.LEAVE:
							currentNode.AsmInstructions.Add(instruction);
							break;

						// Stack instructions
						case CPUInstructionEnum.POP:
							currentNode.AsmInstructions.Add(instruction);
							break;

						case CPUInstructionEnum.POPA:
							currentNode.AsmInstructions.Add(instruction);
							break;

						case CPUInstructionEnum.POPF:
							currentNode.AsmInstructions.Add(instruction);
							break;

						case CPUInstructionEnum.PUSH:
							currentNode.AsmInstructions.Add(instruction);
							break;

						case CPUInstructionEnum.PUSHA:
							currentNode.AsmInstructions.Add(instruction);
							break;

						case CPUInstructionEnum.PUSHF:
							currentNode.AsmInstructions.Add(instruction);
							break;

						// Flag instructions
						case CPUInstructionEnum.CLD:
							currentNode.AsmInstructions.Add(instruction);
							break;

						case CPUInstructionEnum.STD:
							currentNode.AsmInstructions.Add(instruction);
							break;

						case CPUInstructionEnum.CLC:
							currentNode.AsmInstructions.Add(instruction);
							break;

						case CPUInstructionEnum.STC:
							currentNode.AsmInstructions.Add(instruction);
							break;

						case CPUInstructionEnum.CMC:
							currentNode.AsmInstructions.Add(instruction);
							break;

						case CPUInstructionEnum.CLI:
							currentNode.AsmInstructions.Add(instruction);
							break;

						case CPUInstructionEnum.STI:
							currentNode.AsmInstructions.Add(instruction);
							break;

						// Exchange data instruction
						case CPUInstructionEnum.XCHG:
							currentNode.AsmInstructions.Add(instruction);
							break;

						// Input and output port instructions
						case CPUInstructionEnum.IN:
							currentNode.AsmInstructions.Add(instruction);
							break;

						case CPUInstructionEnum.OUT:
							currentNode.AsmInstructions.Add(instruction);
							break;

						// Special syntetic functions
						case CPUInstructionEnum.WordsToDword:
							currentNode.AsmInstructions.Add(instruction);
							break;

						// Flow control instructions
						case CPUInstructionEnum.Jcc:
							if (currentNode.LinearAddress == instruction.LinearAddress)
							{
								currentNode.NodeType = FlowGraphNodeTypeEnum.Block;
								currentNode.AsmInstructions.Add(instruction);

								currentNode.ChildNodes.Add(CreateOrFindNode(MainProgram.ToLinearAddress(instruction.Segment, instruction.Parameters[1].Value), 
									FlowGraphNodeTypeEnum.Block, unprocessedNodes, true));

								instructionPos++;
								if (instructionPos < instructionCount)
								{
									instruction = this.parentFunction.AsmInstructions[instructionPos];
									currentNode.ChildNodes.Add(CreateOrFindNode(instruction.LinearAddress, FlowGraphNodeTypeEnum.Block, unprocessedNodes, true));
								}
								else
								{
									throw new Exception($"Expected next instruction in {currentNode.NodeType} block");
								}
							}
							else if (!this.nodes.ContainsKey(instruction.LinearAddress))
							{
								newNode = CreateOrFindNode(instruction.LinearAddress, FlowGraphNodeTypeEnum.Block, unprocessedNodes, false);
								newNode.AsmInstructions.Add(instruction);
								currentNode.ChildNodes.Add(newNode);

								newNode.ChildNodes.Add(CreateOrFindNode(MainProgram.ToLinearAddress(instruction.Segment, instruction.Parameters[1].Value), 
									FlowGraphNodeTypeEnum.Block, unprocessedNodes, true));

								instructionPos++;
								if (instructionPos < instructionCount)
								{
									instruction = this.parentFunction.AsmInstructions[instructionPos];
									newNode.ChildNodes.Add(CreateOrFindNode(instruction.LinearAddress, FlowGraphNodeTypeEnum.Block, unprocessedNodes, true));
								}
								else
								{
									throw new Exception($"Expected next instruction in {newNode.NodeType} block");
								}
							}
							else
							{
								currentNode.ChildNodes.Add(this.nodes.GetValueByKey(instruction.LinearAddress));
							}

							blockEnd = true;
							break;

						case CPUInstructionEnum.JCXZ:
							if (currentNode.LinearAddress == instruction.LinearAddress)
							{
								currentNode.NodeType = FlowGraphNodeTypeEnum.Block;
								currentNode.AsmInstructions.Add(instruction);

								currentNode.ChildNodes.Add(CreateOrFindNode(MainProgram.ToLinearAddress(instruction.Segment, instruction.Parameters[0].Value),
									FlowGraphNodeTypeEnum.Block, unprocessedNodes, true));

								instructionPos++;
								if (instructionPos < instructionCount)
								{
									instruction = this.parentFunction.AsmInstructions[instructionPos];
									currentNode.ChildNodes.Add(CreateOrFindNode(instruction.LinearAddress, FlowGraphNodeTypeEnum.Block, unprocessedNodes, true));
								}
								else
								{
									throw new Exception($"Expected next instruction in {currentNode.NodeType} block");
								}
							}
							else if (!this.nodes.ContainsKey(instruction.LinearAddress))
							{
								newNode = CreateOrFindNode(instruction.LinearAddress, FlowGraphNodeTypeEnum.Block, unprocessedNodes, false);
								newNode.AsmInstructions.Add(instruction);
								currentNode.ChildNodes.Add(newNode);

								newNode.ChildNodes.Add(CreateOrFindNode(MainProgram.ToLinearAddress(instruction.Segment, instruction.Parameters[0].Value), 
									FlowGraphNodeTypeEnum.Block, unprocessedNodes, true));

								instructionPos++;
								if (instructionPos < instructionCount)
								{
									instruction = this.parentFunction.AsmInstructions[instructionPos];
									newNode.ChildNodes.Add(CreateOrFindNode(instruction.LinearAddress, FlowGraphNodeTypeEnum.Block, unprocessedNodes, true));
								}
								else
								{
									throw new Exception($"Expected next instruction in {newNode.NodeType} block");
								}
							}
							else
							{
								currentNode.ChildNodes.Add(this.nodes.GetValueByKey(instruction.LinearAddress));
							}

							blockEnd = true;
							break;

						case CPUInstructionEnum.LOOP:
							if (currentNode.LinearAddress == instruction.LinearAddress)
							{
								currentNode.NodeType = FlowGraphNodeTypeEnum.Block;
								currentNode.AsmInstructions.Add(instruction);

								currentNode.ChildNodes.Add(CreateOrFindNode(MainProgram.ToLinearAddress(instruction.Segment, instruction.Parameters[0].Value), 
									FlowGraphNodeTypeEnum.Block, unprocessedNodes, true));

								instructionPos++;
								if (instructionPos < instructionCount)
								{
									instruction = this.parentFunction.AsmInstructions[instructionPos];
									currentNode.ChildNodes.Add(CreateOrFindNode(instruction.LinearAddress, FlowGraphNodeTypeEnum.Block, unprocessedNodes, true));
								}
								else
								{
									throw new Exception($"Expected next instruction in {currentNode.NodeType} block");
								}
							}
							else if (!this.nodes.ContainsKey(instruction.LinearAddress))
							{
								newNode = CreateOrFindNode(instruction.LinearAddress, FlowGraphNodeTypeEnum.Block, unprocessedNodes, false);
								newNode.AsmInstructions.Add(instruction);
								currentNode.ChildNodes.Add(newNode);

								newNode.ChildNodes.Add(CreateOrFindNode(MainProgram.ToLinearAddress(instruction.Segment, instruction.Parameters[0].Value), 
									FlowGraphNodeTypeEnum.Block, unprocessedNodes, true));

								instructionPos++;
								if (instructionPos < instructionCount)
								{
									instruction = this.parentFunction.AsmInstructions[instructionPos];
									newNode.ChildNodes.Add(CreateOrFindNode(instruction.LinearAddress, FlowGraphNodeTypeEnum.Block, unprocessedNodes, true));
								}
								else
								{
									throw new Exception($"Expected next instruction in {newNode.NodeType} block");
								}
							}
							else
							{
								currentNode.ChildNodes.Add(this.nodes.GetValueByKey(instruction.LinearAddress));
							}

							blockEnd = true;
							break;

						case CPUInstructionEnum.JMP:
							parameter = instruction.Parameters[0];
							if (parameter.Type == CPUParameterTypeEnum.Immediate)
							{
								newNode = CreateOrFindNode(MainProgram.ToLinearAddress(instruction.Segment, instruction.Parameters[0].Value), 
									FlowGraphNodeTypeEnum.Block, unprocessedNodes, true);
								currentNode.ChildNodes.Add(newNode);

								blockEnd = true;
							}
							else
							{
								currentNode.AsmInstructions.Add(instruction);
							}

							break;

						case CPUInstructionEnum.JMPF:
							parameter = instruction.Parameters[0];
							if (parameter.Type == CPUParameterTypeEnum.SegmentOffset)
							{
								newNode = CreateOrFindNode(MainProgram.ToLinearAddress(parameter.Segment, parameter.Value), FlowGraphNodeTypeEnum.Block, unprocessedNodes, true);
								currentNode.ChildNodes.Add(newNode);

								blockEnd = true;
							}
							else
							{
								currentNode.AsmInstructions.Add(instruction);
							}
							break;

						case CPUInstructionEnum.SWITCH:
							if (currentNode.LinearAddress == instruction.LinearAddress)
							{
								currentNode.NodeType = FlowGraphNodeTypeEnum.Switch;
								currentNode.AsmInstructions.Add(instruction);

								for (int i = 1; i < instruction.Parameters.Count; i++)
								{
									currentNode.SwitchValues.Add((int)instruction.Parameters[i].Value);
									currentNode.ChildNodes.Add(CreateOrFindNode(MainProgram.ToLinearAddress(instruction.Segment, (uint)instruction.Parameters[i].Displacement),
										FlowGraphNodeTypeEnum.Block, unprocessedNodes, true));
								}
							}
							else if (!this.nodes.ContainsKey(instruction.LinearAddress))
							{
								newNode = CreateOrFindNode(instruction.LinearAddress, FlowGraphNodeTypeEnum.Switch, unprocessedNodes, false);
								newNode.AsmInstructions.Add(instruction);
								currentNode.ChildNodes.Add(newNode);

								for (int i = 1; i < instruction.Parameters.Count; i++)
								{
									newNode.SwitchValues.Add((int)instruction.Parameters[i].Value);
									newNode.ChildNodes.Add(CreateOrFindNode(MainProgram.ToLinearAddress(instruction.Segment, (uint)instruction.Parameters[i].Displacement),
										FlowGraphNodeTypeEnum.Block, unprocessedNodes, true));
								}
							}
							else
							{
								currentNode.ChildNodes.Add(this.nodes.GetValueByKey(instruction.LinearAddress));
							}

							blockEnd = true;
							break;

						case CPUInstructionEnum.CALL:
							currentNode.AsmInstructions.Add(instruction);

							if (instructionPos + 1 < instructionCount)
							{
								// also add ADD SP, number instruction
								if ((instruction = this.parentFunction.AsmInstructions[instructionPos + 1]).InstructionType == CPUInstructionEnum.ADD &&
									instruction.Parameters.Count == 2 &&
									instruction.Parameters[0].Type == CPUParameterTypeEnum.Register &&
									instruction.Parameters[0].RegisterValue == CPURegisterEnum.SP)
								{
									currentNode.AsmInstructions.Add(instruction);
									instructionPos++;
								}
							}

							if (instructionPos + 1 < instructionCount)
							{
								newNode = CreateOrFindNode(this.parentFunction.AsmInstructions[instructionPos + 1].LinearAddress, FlowGraphNodeTypeEnum.Block, unprocessedNodes, true);
								currentNode.ChildNodes.Add(newNode);
							}

							blockEnd = true;
							break;

						case CPUInstructionEnum.CALLF:
							currentNode.AsmInstructions.Add(instruction);

							if (instructionPos + 1 < instructionCount)
							{
								// also add ADD SP, number instruction
								if ((instruction = this.parentFunction.AsmInstructions[instructionPos + 1]).InstructionType == CPUInstructionEnum.ADD &&
									instruction.Parameters.Count == 2 &&
									instruction.Parameters[0].Type == CPUParameterTypeEnum.Register &&
									instruction.Parameters[0].RegisterValue == CPURegisterEnum.SP)
								{
									currentNode.AsmInstructions.Add(instruction);
									instructionPos++;
								}
							}

							if (instructionPos + 1 < instructionCount)
							{
								newNode = CreateOrFindNode(this.parentFunction.AsmInstructions[instructionPos + 1].LinearAddress, FlowGraphNodeTypeEnum.Block, unprocessedNodes, true);
								currentNode.ChildNodes.Add(newNode);
							}

							blockEnd = true;
							break;

						case CPUInstructionEnum.CallOverlay:
							currentNode.AsmInstructions.Add(instruction);

							if (instructionPos + 1 < instructionCount)
							{
								// also add ADD SP, number instruction
								if ((instruction = this.parentFunction.AsmInstructions[instructionPos + 1]).InstructionType == CPUInstructionEnum.ADD &&
									instruction.Parameters.Count == 2 &&
									instruction.Parameters[0].Type == CPUParameterTypeEnum.Register &&
									instruction.Parameters[0].RegisterValue == CPURegisterEnum.SP)
								{
									currentNode.AsmInstructions.Add(instruction);
									instructionPos++;
								}
							}

							if (instructionPos + 1 < instructionCount)
							{
								newNode = CreateOrFindNode(this.parentFunction.AsmInstructions[instructionPos + 1].LinearAddress, FlowGraphNodeTypeEnum.Block, unprocessedNodes, true);
								currentNode.ChildNodes.Add(newNode);
							}

							blockEnd = true;
							break;

						case CPUInstructionEnum.INT:
							currentNode.AsmInstructions.Add(instruction);

							if (instructionPos + 1 < instructionCount)
							{
								newNode = CreateOrFindNode(this.parentFunction.AsmInstructions[instructionPos + 1].LinearAddress, FlowGraphNodeTypeEnum.Block, unprocessedNodes, true);
								currentNode.ChildNodes.Add(newNode);
							}

							blockEnd = true;
							break;

						case CPUInstructionEnum.RET:
						case CPUInstructionEnum.RETF:
						case CPUInstructionEnum.IRET:
							if (this.endNodes.ContainsKey(instruction.LinearAddress))
							{
								currentNode.ChildNodes.Add(this.endNodes.GetValueByKey(instruction.LinearAddress));
							}
							else
							{
								newNode = CreateOrFindNode(instruction.LinearAddress, FlowGraphNodeTypeEnum.End, unprocessedNodes, false);
								newNode.AsmInstructions.Add(instruction);
								this.endNodes.Add(newNode.LinearAddress, newNode);
								currentNode.ChildNodes.Add(newNode);
							}

							blockEnd = true;
							break;

						default:
							Console.WriteLine($"Unexpected instruction type '{instruction.InstructionType}'");
							break;
					}
				}

				if (blockEnd || instructionPos >= instructionCount)
				{
					instructionPos = instructionCount;

					while (unprocessedNodes.Count > 0)
					{
						FlowGraphNode node = unprocessedNodes.Dequeue();

						if (node.LinearAddress != 0)
						{
							instructionPos = this.parentFunction.GetInstructionPositionByLinearAddress(node.LinearAddress);
							if (instructionPos < 0)
							{
								throw new Exception($"Expected next instruction in {node.NodeType} block");
							}
							currentNode = node;
							break;
						}
					}
				}
				else
				{
					instructionPos++;
				}
			}

			// update node references
			for (int i = 0; i < this.nodes.Count; i++)
			{
				this.nodes[i].Value.ReferenceNodes.Clear();
			}

			for (int i = 0; i < this.nodes.Count; i++)
			{
				FlowGraphNode node = this.nodes[i].Value;

				for (int j = 0; j < node.ChildNodes.Count; j++)
				{
					FlowGraphNode childNode = node.ChildNodes[j];

					this.nodes.GetValueByKey(childNode.LinearAddress).ReferenceNodes.Add(node.LinearAddress, node);
				}
			}

			// we want to order the nodes by their physical address for a purpose of assigning a unique ordinal number
			int ordinal = 0;
			FlowGraphNode[] sortedNodes = this.nodes.Values.ToArray();

			Array.Sort<FlowGraphNode>(sortedNodes, (item1, item2) => item1.LinearAddress.CompareTo(item2.LinearAddress));

			for (int i = 0; i < sortedNodes.Length; i++)
			{
				sortedNodes[i].Ordinal = ordinal++;
			}

			// check that all nodes have children, except end node
			for (int i = 0; i < this.nodes.Count; i++)
			{
				FlowGraphNode node = this.nodes[i].Value;

				if (node.NodeType != FlowGraphNodeTypeEnum.End && node.ChildNodes.Count == 0)
				{
					throw new Exception($"All nodes should have their children, except end node. " +
						$"The childless node address: 0x{node.LinearAddress:x} in function {this.parentFunction.ParentSegment.Name}.{this.parentFunction.Name}");
				}
			}
		}

		private void ConstructLocalRequirements()
		{
			// construct local node requirements
			for (int i = 0; i < this.nodes.Count; i++)
			{
				this.nodes[i].Value.SetLocalRequirements();
			}
		}

		private void DetermineFunctionBPFrame()
		{
			#region Try to consolidate multiple end nodes
			while (this.endNodes.Count > 1)
			{
				FlowGraphNode endNode1 = this.endNodes[0].Value;
				FlowGraphNode endNode2 = this.endNodes[1].Value;

				if (endNode1.Equals(endNode2))
				{
					// we can safely consolidate nodes
					if (endNode1.LinearAddress > endNode2.LinearAddress)
					{
						while (endNode2.ReferenceNodes.Count > 0)
						{
							FlowGraphNode refNode = endNode2.ReferenceNodes[0].Value;

							endNode1.ReferenceNodes.Add(refNode.LinearAddress, refNode);
							endNode2.ReferenceNodes.RemoveAt(0);

							for (int i = 0; i < refNode.ChildNodes.Count; i++)
							{
								if (refNode.ChildNodes[i].LinearAddress == endNode2.LinearAddress)
								{
									refNode.ChildNodes[i] = endNode1;
									break;
								}
							}
						}

						endNode2.ChildNodes.Clear();
						endNode2.AsmInstructions.Clear();
						this.nodes.RemoveByKey(endNode2.LinearAddress);
						this.endNodes.RemoveByKey(endNode2.LinearAddress);
					}
					else
					{
						while (endNode1.ReferenceNodes.Count > 0)
						{
							FlowGraphNode refNode = endNode1.ReferenceNodes[0].Value;

							endNode2.ReferenceNodes.Add(refNode.LinearAddress, refNode);
							endNode1.ReferenceNodes.RemoveAt(0);

							for (int i = 0; i < refNode.ChildNodes.Count; i++)
							{
								if (refNode.ChildNodes[i].LinearAddress == endNode1.LinearAddress)
								{
									refNode.ChildNodes[i] = endNode2;
									break;
								}
							}
						}

						endNode1.ChildNodes.Clear();
						endNode1.AsmInstructions.Clear();
						this.nodes.RemoveByKey(endNode1.LinearAddress);
						this.endNodes.RemoveByKey(endNode1.LinearAddress);
					}
				}
				else
				{
					break;
				}
			}
			#endregion

			if (this.startNode != null && this.endNodes.Count == 1)
			{
				FlowGraphNode startNode = this.startNode;
				FlowGraphNode endNode = this.endNodes[0].Value;
				CPUInstruction instruction;

				if (startNode.ReferenceNodes.Count == 0 && startNode.ChildNodes.Count == 1)
				{
					FlowGraphNode startNode1 = startNode.ChildNodes[0];

					bool endNodeBPFrame = true;

					for (int i = 0; i < endNode.ReferenceNodes.Count; i++)
					{
						FlowGraphNode endNode1 = endNode.ReferenceNodes[i].Value;
						int endNode1InstructionCount = endNode1.AsmInstructions.Count;

						if (endNode1InstructionCount > 0 &&
							(instruction = endNode1.AsmInstructions[endNode1InstructionCount - 1]).InstructionType == CPUInstructionEnum.POP &&
							instruction.OperandSize == CPUParameterSizeEnum.UInt16 &&
							instruction.Parameters.Count == 1 &&
							instruction.Parameters[0].Type == CPUParameterTypeEnum.Register &&
							instruction.Parameters[0].Value == (uint)CPURegisterEnum.BP)
						{
							endNodeBPFrame = true;
						}
						else
						{
							endNodeBPFrame = false;
							break;
						}
					}

					if (endNodeBPFrame && startNode1.AsmInstructions.Count > 4 &&
						(instruction = startNode1.AsmInstructions[0]).InstructionType == CPUInstructionEnum.PUSH &&
						instruction.OperandSize == CPUParameterSizeEnum.UInt16 &&
						instruction.Parameters.Count == 1 &&
						instruction.Parameters[0].Type == CPUParameterTypeEnum.Register &&
						instruction.Parameters[0].Value == (uint)CPURegisterEnum.BP &&

						(instruction = startNode1.AsmInstructions[1]).InstructionType == CPUInstructionEnum.MOV &&
						instruction.OperandSize == CPUParameterSizeEnum.UInt16 &&
						instruction.Parameters.Count == 2 &&
						instruction.Parameters[0].Type == CPUParameterTypeEnum.Register &&
						instruction.Parameters[0].Value == (uint)CPURegisterEnum.BP &&
						instruction.Parameters[1].Type == CPUParameterTypeEnum.Register &&
						instruction.Parameters[1].Value == (uint)CPURegisterEnum.SP)
					{
						// function satisfies basic C language frame
						this.hasBPFrame = true;

						#region Adjust start and end block instruction positions
						// move frame instructions to start and end block
						startNode.AsmInstructions.Add(startNode1.AsmInstructions[0]);
						startNode1.AsmInstructions.RemoveAt(0);
						startNode.AsmInstructions.Add(startNode1.AsmInstructions[0]);
						startNode1.AsmInstructions.RemoveAt(0);

						// Move required and verified POP BP instruction to endNode
						for (int i = 0; i < endNode.ReferenceNodes.Count; i++)
						{
							FlowGraphNode endNode1 = endNode.ReferenceNodes[i].Value;
							int endNode1InstructionCount = endNode1.AsmInstructions.Count;
							CPUInstruction instruction1 = endNode1.AsmInstructions[endNode1InstructionCount - 1];

							if (i == 0)
							{
								endNode.AsmInstructions.Insert(0, instruction1);
							}

							endNode1.AsmInstructions.RemoveAt(endNode1InstructionCount - 1);
						}

						if (startNode1.AsmInstructions.Count > 0 &&
							(instruction = startNode1.AsmInstructions[0]).InstructionType == CPUInstructionEnum.SUB &&
							instruction.OperandSize == CPUParameterSizeEnum.UInt16 &&
							instruction.Parameters.Count == 2 &&
							instruction.Parameters[0].Type == CPUParameterTypeEnum.Register &&
							instruction.Parameters[0].Value == (uint)CPURegisterEnum.SP &&
							instruction.Parameters[1].Type == CPUParameterTypeEnum.Immediate)
						{
							startNode.AsmInstructions.Add(instruction);
							startNode1.AsmInstructions.RemoveAt(0);

							this.parentFunction.LocalVariableSize = (int)instruction.Parameters[1].Value;
							this.parentFunction.LocalVariablePosition += this.parentFunction.LocalVariableSize;
						}

						// move MOV SP, BP instruction in endBlock
						for (int i = 0; i < endNode.ReferenceNodes.Count; i++)
						{
							FlowGraphNode endNode1 = endNode.ReferenceNodes[i].Value;
							int endNode1InstructionCount = endNode1.AsmInstructions.Count;

							if (endNode1InstructionCount > 0)
							{
								CPUInstruction instruction1 = endNode1.AsmInstructions[endNode1InstructionCount - 1];

								if (!(instruction1.InstructionType == CPUInstructionEnum.MOV &&
									instruction1.OperandSize == CPUParameterSizeEnum.UInt16 &&
									instruction1.Parameters.Count == 2 &&
									instruction1.Parameters[0].Type == CPUParameterTypeEnum.Register &&
									instruction1.Parameters[0].Value == (uint)CPURegisterEnum.SP &&
									instruction1.Parameters[1].Type == CPUParameterTypeEnum.Register &&
									instruction1.Parameters[1].Value == (uint)CPURegisterEnum.BP))
								{
									if (i == 0)
									{
										break;
									}
									else
									{
										throw new Exception($"Can't match start and end block BP frame instructions");
									}
								}

								if (i == 0)
								{
									endNode.AsmInstructions.Insert(0, instruction1);
								}

								endNode1.AsmInstructions.RemoveAt(endNode1InstructionCount - 1);
							}
						}

						while (startNode1.AsmInstructions.Count > 0)
						{
							instruction = startNode1.AsmInstructions[0];

							if (instruction.InstructionType == CPUInstructionEnum.PUSH)
							{
								CPUParameter parameter = instruction.Parameters[0];

								if (parameter.Type == CPUParameterTypeEnum.Register && parameter.Size == CPUParameterSizeEnum.UInt16)
								{
									bool endBlock = true;

									switch ((CPURegisterEnum)parameter.Value)
									{
										case CPURegisterEnum.SI:
											if (this.usesSI)
											{
												throw new Exception($"In function {this.parentFunction.ParentSegment.Name}.{this.parentFunction.Name}, the SI register is pushed more than once onto the stack");
											}
											this.usesSI = true;
											endBlock = false;

											startNode.AsmInstructions.Add(instruction);
											startNode1.AsmInstructions.RemoveAt(0);

											// move matched instruction to endBlock
											for (int i = 0; i < endNode.ReferenceNodes.Count; i++)
											{
												FlowGraphNode endNode1 = endNode.ReferenceNodes[i].Value;
												int endNode1InstructionCount = endNode1.AsmInstructions.Count;
												CPUInstruction instruction1 = endNode1.AsmInstructions[endNode1InstructionCount - 1];

												if (instruction1.InstructionType != CPUInstructionEnum.POP || 
													instruction.Parameters.Count != instruction1.Parameters.Count ||
													!instruction.Parameters[0].Equals(instruction1.Parameters[0]))
												{
													throw new Exception($"Can't match start and end block BP frame instructions");
												}

												if (i == 0)
												{
													endNode.AsmInstructions.Insert(0, instruction1);
												}

												endNode1.AsmInstructions.RemoveAt(endNode1InstructionCount - 1);
											}
											break;

										case CPURegisterEnum.DI:
											if (this.usesDI)
											{
												throw new Exception($"In function {this.parentFunction.ParentSegment.Name}.{this.parentFunction.Name}, the DI register is pushed more than once onto the stack");
											}
											this.usesDI = true;
											endBlock = false;

											startNode.AsmInstructions.Add(instruction);
											startNode1.AsmInstructions.RemoveAt(0);

											// move matched instruction to endBlock
											for (int i = 0; i < endNode.ReferenceNodes.Count; i++)
											{
												FlowGraphNode endNode1 = endNode.ReferenceNodes[i].Value;
												int endNode1InstructionCount = endNode1.AsmInstructions.Count;
												CPUInstruction instruction1 = endNode1.AsmInstructions[endNode1InstructionCount - 1];

												if (instruction1.InstructionType != CPUInstructionEnum.POP ||
													instruction.Parameters.Count != instruction1.Parameters.Count ||
													!instruction.Parameters[0].Equals(instruction1.Parameters[0]))
												{
													throw new Exception($"Can't match start and end block BP frame instructions");
												}

												if (i == 0)
												{
													endNode.AsmInstructions.Insert(0, instruction1);
												}

												endNode1.AsmInstructions.RemoveAt(endNode1InstructionCount - 1);
											}
											break;
									}

									if (endBlock)
									{
										break;
									}
								}
								else if (parameter.Type == CPUParameterTypeEnum.SegmentRegister && parameter.Size == CPUParameterSizeEnum.UInt16 &&
									(CPUSegmentRegisterEnum)parameter.Value == CPUSegmentRegisterEnum.DS)
								{
									CPUInstruction instruction1;

									// sometimes at the start of the function PUSH DS, POP ES is used
									if (startNode1.AsmInstructions.Count > 1 &&
										(instruction1 = startNode1.AsmInstructions[1]).InstructionType != CPUInstructionEnum.POP)
									{
										if (this.usesDS)
										{
											throw new Exception($"In function {this.parentFunction.ParentSegment.Name}.{this.parentFunction.Name}, the DS segment register is pushed more than once onto the stack");
										}

										this.usesDS = true;

										startNode.AsmInstructions.Add(instruction);
										startNode1.AsmInstructions.RemoveAt(0);

										// move matched instruction to endBlock
										for (int i = 0; i < endNode.ReferenceNodes.Count; i++)
										{
											FlowGraphNode endNode1 = endNode.ReferenceNodes[i].Value;
											int endNode1InstructionCount = endNode1.AsmInstructions.Count;
											instruction1 = endNode1.AsmInstructions[endNode1InstructionCount - 1];

											if (instruction1.InstructionType != CPUInstructionEnum.POP ||
												instruction.Parameters.Count != instruction1.Parameters.Count ||
												!instruction.Parameters[0].Equals(instruction1.Parameters[0]))
											{
												throw new Exception($"Can't match start and end block BP frame instructions");
											}

											if (i == 0)
											{
												endNode.AsmInstructions.Insert(0, instruction1);
											}

											endNode1.AsmInstructions.RemoveAt(endNode1InstructionCount - 1);
										}
									}
									else
									{
										break;
									}
								}
								else
								{
									break;
								}
							}
							else
							{
								break;
							}
						}
						#endregion
					}
				}
			}
		}

		public void TranslateFunction()
		{
			// refuse to process assembly and library functions
			if (!this.parentFunction.IsLibraryFunction && this.hasBPFrame && this.startNode != null && this.endNodes.Count > 0)
			{
				UndoCompilerOptimizations();
				//TranslateToIL();
				//DetermineBasicLanguageConstructionBlocks();
			}
		}

		private void UndoCompilerOptimizations()
		{
			#region Check and move ADD SP, ? instruction after each CALL instruction
			for (int i = 0; i < this.nodes.Count; i++)
			{
				FlowGraphNode node = this.nodes[i].Value;

				if (node.NodeType != FlowGraphNodeTypeEnum.Start && node.NodeType != FlowGraphNodeTypeEnum.End)
				{
					for (int j = 0; j < node.AsmInstructions.Count; j++)
					{
						CPUInstruction instruction = node.AsmInstructions[j];
						CPUInstruction? spInstruction;
						CPUParameter parameter;
						ProgramFunction? function;

						switch (instruction.InstructionType)
						{
							case CPUInstructionEnum.CALL:
								parameter = instruction.Parameters[0];
								function = this.parentFunction.ParentSegment.ParentProgram.FindFunction(0, instruction.Segment, (ushort)parameter.Value);

								if (function != null)
								{
									if (function.ParameterSize > 0)
									{
										spInstruction = FindAndAdjustAddSPCallInstruction(function, node, j);

										if (spInstruction == null)
										{
											Console.WriteLine($"Warning, can't find ADD SP, ? instruction ater CALL instruction, " +
												$"to function {function.ParentSegment.Name}.{function.Name} at offset 0x{instruction.Offset:x} in " +
												$"function {this.parentFunction.ParentSegment.Name}.{this.parentFunction.Name}");
										}
										else if (spInstruction.Parameters[1].Value != function.ParameterSize &&
											((function.FunctionOptions & ProgramFunctionOptionsEnum.VariableArguments) != ProgramFunctionOptionsEnum.VariableArguments ||
											spInstruction.Parameters[1].Value < function.ParameterSize))
										{
											Console.WriteLine($"Warning, parameter size doesn't match function call to {function.ParentSegment.Name}.{function.Name} ({function.ParameterSize}), " +
												$"in function {this.parentFunction.ParentSegment.Name}.{this.parentFunction.Name} at offset 0x{instruction.Offset:x} ({spInstruction.Parameters[1].Value})");
										}
									}
								}
								break;

							case CPUInstructionEnum.CALLF:
								parameter = instruction.Parameters[0];
								function = this.parentFunction.ParentSegment.ParentProgram.FindFunction(0, instruction.Segment, (ushort)parameter.Value);

								if (function != null)
								{
									if (function.ParameterSize > 0)
									{
										spInstruction = FindAndAdjustAddSPCallInstruction(function, node, j);

										if (spInstruction == null)
										{
											Console.WriteLine($"Warning, can't find ADD SP, ? instruction ater CALL instruction, " +
												$"to function {function.ParentSegment.Name}.{function.Name} at offset 0x{instruction.Offset:x} in " +
												$"function {this.parentFunction.ParentSegment.Name}.{this.parentFunction.Name}");
										}
										else if (spInstruction.Parameters[1].Value != function.ParameterSize)
										{
											Console.WriteLine($"Warning, parameter size doesn't match function call to {function.ParentSegment.Name}.{function.Name} ({function.ParameterSize}), " +
												$"in function {this.parentFunction.ParentSegment.Name}.{this.parentFunction.Name} at offset 0x{instruction.Offset:x} ({spInstruction.Parameters[1].Value})");
										}
									}
								}
								break;

							case CPUInstructionEnum.CallOverlay:
								function = this.parentFunction.ParentSegment.ParentProgram.FindFunction((ushort)instruction.Parameters[0].Value, 0, (ushort)instruction.Parameters[1].Value);

								if (function != null)
								{
									if (function.ParameterSize > 0)
									{
										spInstruction = FindAndAdjustAddSPCallInstruction(function, node, j);

										if (spInstruction == null)
										{
											Console.WriteLine($"Warning, can't find ADD SP, ? instruction ater CALL instruction, " +
												$"to function {function.ParentSegment.Name}.{function.Name} at offset 0x{instruction.Offset:x} in " +
												$"function {this.parentFunction.ParentSegment.Name}.{this.parentFunction.Name}");
										}
										else if (spInstruction.Parameters[1].Value != function.ParameterSize)
										{
											Console.WriteLine($"Warning, parameter size doesn't match function call to {function.ParentSegment.Name}.{function.Name} ({function.ParameterSize}), " +
												$"in function {this.parentFunction.ParentSegment.Name}.{this.parentFunction.Name} at offset 0x{instruction.Offset:x} ({spInstruction.Parameters[1].Value})");
										}
									}
								}
								break;
						}
					}
				}
			}
			#endregion
		}

		private CPUInstruction? FindAndAdjustAddSPCallInstruction(ProgramFunction targetFunction, FlowGraphNode node, int instructionIndex)
		{
			CPUInstruction? spInstruction = null;

			if (instructionIndex + 1 < node.AsmInstructions.Count)
			{
				CPUInstruction instruction = node.AsmInstructions[instructionIndex + 1];

				if (instruction.InstructionType == CPUInstructionEnum.ADD &&
					instruction.Parameters.Count == 2 &&
					instruction.Parameters[0].Type == CPUParameterTypeEnum.Register &&
					instruction.Parameters[0].RegisterValue == CPURegisterEnum.SP)
				{
					spInstruction = instruction;
				}
				else if (targetFunction.ParameterSize == 2 &&
					instruction.InstructionType == CPUInstructionEnum.POP &&
					instruction.Parameters.Count == 1 &&
					instruction.Parameters[0].Type == CPUParameterTypeEnum.Register)
				{
					spInstruction = new CPUInstruction(instruction.Segment, instruction.Offset, CPUInstructionEnum.ADD, instruction.DefaultSize);
					spInstruction.Parameters.Add(new CPUParameter(CPUParameterTypeEnum.Register, (uint)CPURegisterEnum.SP));
					spInstruction.Parameters.Add(new CPUParameter(CPUParameterTypeEnum.Immediate, CPUParameterSizeEnum.UInt16, 2));

					node.AsmInstructions[instructionIndex + 1] = spInstruction;
				}
			}

			if (spInstruction == null && node.ChildNodes.Count == 1 && targetFunction.ParameterSize > 0)
			{
				FlowGraphNode currentNode = node.ChildNodes[0];
				Queue<FlowGraphNode> parentNodes = new();

				parentNodes.Enqueue(currentNode);

				// search n levels for required instruction
				for (int i = 0; i < 2; i++)
				{
					if (currentNode.NodeType == FlowGraphNodeTypeEnum.End)
					{
						// we are forgiving missing ADD SP, ? instruction at the end of function
						CPUInstruction instruction = node.AsmInstructions[node.AsmInstructions.Count - 1];

						spInstruction = new CPUInstruction(instruction.Segment, (ushort)(instruction.Offset + 1), CPUInstructionEnum.ADD, instruction.DefaultSize);
						spInstruction.Parameters.Add(new CPUParameter(CPUParameterTypeEnum.Register, (uint)CPURegisterEnum.SP));
						spInstruction.Parameters.Add(new CPUParameter(CPUParameterTypeEnum.Immediate, CPUParameterSizeEnum.UInt16, (uint)targetFunction.ParameterSize));

						node.AsmInstructions.Add(spInstruction);
					}
					else
					{
						for (int j = 0; j < currentNode.AsmInstructions.Count; j++)
						{
							CPUInstruction instruction = currentNode.AsmInstructions[j];

							if (instruction.InstructionType == CPUInstructionEnum.ADD &&
								instruction.Parameters.Count == 2 &&
								instruction.Parameters[0].Type == CPUParameterTypeEnum.Register &&
								instruction.Parameters[0].RegisterValue == CPURegisterEnum.SP)
							{
								spInstruction = instruction;

								currentNode.AsmInstructions.RemoveAt(j);

								while (parentNodes.Count > 0)
								{
									FlowGraphNode parentNode = parentNodes.Dequeue();

									for (int k = 0; k < parentNode.ReferenceNodes.Count; k++)
									{
										FlowGraphNode referenceNode = parentNode.ReferenceNodes[k].Value;

										referenceNode.AsmInstructions.Add(spInstruction);
									}
								}
								break;
							}
							else if (targetFunction.ParameterSize == 2 &&
								instruction.InstructionType == CPUInstructionEnum.POP &&
								instruction.Parameters.Count == 1 &&
								instruction.Parameters[0].Type == CPUParameterTypeEnum.Register)
							{
								spInstruction = new CPUInstruction(instruction.Segment, instruction.Offset, CPUInstructionEnum.ADD, instruction.DefaultSize);
								spInstruction.Parameters.Add(new CPUParameter(CPUParameterTypeEnum.Register, (uint)CPURegisterEnum.SP));
								spInstruction.Parameters.Add(new CPUParameter(CPUParameterTypeEnum.Immediate, CPUParameterSizeEnum.UInt16, 2));

								currentNode.AsmInstructions.RemoveAt(j);

								while (parentNodes.Count > 0)
								{
									FlowGraphNode parentNode = parentNodes.Dequeue();

									for (int k = 0; k < parentNode.ReferenceNodes.Count; k++)
									{
										FlowGraphNode referenceNode = parentNode.ReferenceNodes[k].Value;

										referenceNode.AsmInstructions.Add(spInstruction);
									}
								}
								break;
							}
						}
					}

					if (spInstruction != null || currentNode.ChildNodes.Count != 1)
					{
						break;
					}
					else
					{
						currentNode = currentNode.ChildNodes[0];
						parentNodes.Enqueue(currentNode);
					}
				}
			}

			return spInstruction;
		}

		public void TranslateToIL()
		{
			BDictionary<CPURegisterEnum, ILExpression> localRegisters = [];
			BDictionary<CPUSegmentRegisterEnum, uint> localSegments = [];
			// track the stack state
			// sometimes the function call doesn't adjust SP (for the length of the parameters) at the end of the function
			Stack<ILExpression> localStack = [];

			//this.parentFunction.LocalVariables.Clear();

			//Console.WriteLine($"Translating '{this.parent.Parent.ToString()}.{this.parent.Name}'");

			localSegments.Add(CPUSegmentRegisterEnum.DS, this.parentFunction.ParentSegment.ParentProgram.DefaultDS);
			localSegments.Add(CPUSegmentRegisterEnum.ES, this.parentFunction.ParentSegment.ParentProgram.DefaultDS);

			for (int i = 0; i < this.nodes.Count; i++)
			{
				this.nodes[i].Value.ClearIL();
			}

			this.parentFunction.TranslatedToIL = TranslateNodeToIL(this.startNode!, localRegisters, localSegments, localStack);
		}

		private bool TranslateNodeToIL(FlowGraphNode node, 
			BDictionary<CPURegisterEnum, ILExpression> localRegisters, 
			BDictionary<CPUSegmentRegisterEnum, uint> localSegments,
			Stack<ILExpression> localStack)
		{
			bool translated = true;

			if (!node.TranslatedToIL)
			{
				if (node.NodeType == FlowGraphNodeTypeEnum.Start)
				{
					// The start node doesn't need to be translated
					node.TranslatedToIL = true;

					for (int i = 0; i < node.ChildNodes.Count; i++)
					{
						translated &= TranslateNodeToIL(node.ChildNodes[i], localRegisters, localSegments, localStack);
					}
				}
				else if (node.NodeType == FlowGraphNodeTypeEnum.End)
				{
					// The end node doesn't need to be translated
					node.TranslatedToIL = true;

					Console.WriteLine("End of function");
				}
				else
				{
					ProgramFunction parentFunction = this.parentFunction;

					// cache and aggregate register values before commiting them to more permanent local or global variables
					BDictionary<CPURegisterEnum, int> cachedRegisters = [];

					// pending comparison state for CMP/TEST -> Jcc translation
					ILExpression? pendingCompareLeft = null;
					ILExpression? pendingCompareRight = null;
					int pendingCompareType = 0; // 0 = CMP, 1 = TEST

					#region Translate Assembly instructions to IL

					for (int i = 0; i < node.AsmInstructions.Count; i++)
					{
						CPUInstruction instruction = node.AsmInstructions[i];
						CPUParameter parameter0;
						CPUParameter parameter1;
						CPURegisterEnum register;
						ILExpression variable;
						ProgramFunction? function;

						switch (instruction.InstructionType)
						{
							case CPUInstructionEnum.SUB:
								parameter0 = instruction.Parameters[0];
								parameter1 = instruction.Parameters[1];

								if (parameter0.Type == CPUParameterTypeEnum.Register)
								{
									if (parameter0.Type == parameter1.Type && parameter0.Value == parameter1.Value)
									{
										register = parameter0.RegisterValue;
										ClearCachedRegister(cachedRegisters, register);

										ILExpression newValue = new ILImmediateValue(parentFunction.ParentSegment.ParentProgram.FromCPUParameterSizeEnum(parameter0.Size), 0);

										if (localRegisters.ContainsKey(register))
										{
											localRegisters.SetValueByKey(register, newValue);
										}
										else
										{
											localRegisters.Add(register, newValue);
										}

										cachedRegisters.Add(register, node.ILInstructions.Count);
									}
									else
									{
										register = parameter0.RegisterValue;

										if (localRegisters.ContainsKey(register) && cachedRegisters.ContainsKey(register))
										{
											ILExpression oldValue = localRegisters.GetValueByKey(register);
											ILExpression newValue = new ILOperator(oldValue, ILOperatorEnum.Substract, ParameterToIL(localSegments, localRegisters, parameter1));

											localRegisters.SetValueByKey(register, newValue);
										}
										else
										{
											throw new Exception($"Use of a undefined local register '{register}'");
										}
									}
								}
								else
								{
									throw new Exception($"Parameter type '{parameter0.Type}' not implemented");
								}
								break;

							case CPUInstructionEnum.ADD:
							case CPUInstructionEnum.ADC:
								parameter0 = instruction.Parameters[0];
								parameter1 = instruction.Parameters[1];

								if (parameter0.Type == CPUParameterTypeEnum.Register)
								{
									register = parameter0.RegisterValue;

									if (localRegisters.ContainsKey(register) && cachedRegisters.ContainsKey(register))
									{
										ILExpression oldValue = localRegisters.GetValueByKey(register);
										ILExpression newValue = new ILOperator(oldValue, ILOperatorEnum.Add, ParameterToIL(localSegments, localRegisters, parameter1));

										localRegisters.SetValueByKey(register, newValue);
									}
									else
									{
										throw new Exception($"Use of a undefined local register '{register}'");
									}
								}
								else
								{
									throw new Exception($"Parameter type '{parameter0.Type}' not implemented");
								}
								break;

							case CPUInstructionEnum.AND:
								parameter0 = instruction.Parameters[0];
								parameter1 = instruction.Parameters[1];

								if (parameter0.Type == CPUParameterTypeEnum.Register)
								{
									register = parameter0.RegisterValue;

									if (localRegisters.ContainsKey(register) && cachedRegisters.ContainsKey(register))
									{
										ILExpression oldValue = localRegisters.GetValueByKey(register);
										ILExpression newValue = new ILOperator(oldValue, ILOperatorEnum.BitwiseAnd, ParameterToIL(localSegments, localRegisters, parameter1));

										localRegisters.SetValueByKey(register, newValue);
									}
									else
									{
										throw new Exception($"Use of a undefined local register '{register}'");
									}
								}
								else
								{
									throw new Exception($"Parameter type '{parameter0.Type}' not implemented");
								}
								break;

							case CPUInstructionEnum.OR:
								parameter0 = instruction.Parameters[0];
								parameter1 = instruction.Parameters[1];

								if (parameter0.Type == CPUParameterTypeEnum.Register)
								{
									register = parameter0.RegisterValue;

									if (localRegisters.ContainsKey(register) && cachedRegisters.ContainsKey(register))
									{
										ILExpression oldValue = localRegisters.GetValueByKey(register);
										ILExpression newValue = new ILOperator(oldValue, ILOperatorEnum.BitwiseOr, ParameterToIL(localSegments, localRegisters, parameter1));

										localRegisters.SetValueByKey(register, newValue);
									}
									else
									{
										throw new Exception($"Use of a undefined local register '{register}'");
									}
								}
								else
								{
									throw new Exception($"Parameter type '{parameter0.Type}' not implemented");
								}
								break;

							case CPUInstructionEnum.XOR:
								parameter0 = instruction.Parameters[0];
								parameter1 = instruction.Parameters[1];

								if (parameter0.Type == CPUParameterTypeEnum.Register)
								{
									if (parameter0.Type == parameter1.Type && parameter0.Value == parameter1.Value)
									{
										// XOR reg, reg is a zero idiom
										register = parameter0.RegisterValue;
										ClearCachedRegister(cachedRegisters, register);

										ILExpression newValue = new ILImmediateValue(parentFunction.ParentSegment.ParentProgram.FromCPUParameterSizeEnum(parameter0.Size), 0);

										if (localRegisters.ContainsKey(register))
										{
											localRegisters.SetValueByKey(register, newValue);
										}
										else
										{
											localRegisters.Add(register, newValue);
										}

										cachedRegisters.Add(register, node.ILInstructions.Count);
									}
									else
									{
										register = parameter0.RegisterValue;

										if (localRegisters.ContainsKey(register) && cachedRegisters.ContainsKey(register))
										{
											ILExpression oldValue = localRegisters.GetValueByKey(register);
											ILExpression newValue = new ILOperator(oldValue, ILOperatorEnum.BitwiseXor, ParameterToIL(localSegments, localRegisters, parameter1));

											localRegisters.SetValueByKey(register, newValue);
										}
										else
										{
											throw new Exception($"Use of a undefined local register '{register}'");
										}
									}
								}
								else
								{
									throw new Exception($"Parameter type '{parameter0.Type}' not implemented");
								}
								break;

							case CPUInstructionEnum.SHL:
								parameter0 = instruction.Parameters[0];
								parameter1 = instruction.Parameters[1];

								if (parameter0.Type == CPUParameterTypeEnum.Register)
								{
									register = parameter0.RegisterValue;

									if (localRegisters.ContainsKey(register) && cachedRegisters.ContainsKey(register))
									{
										ILExpression oldValue = localRegisters.GetValueByKey(register);
										ILExpression newValue = new ILOperator(oldValue, ILOperatorEnum.ShiftLeft, ParameterToIL(localSegments, localRegisters, parameter1));

										localRegisters.SetValueByKey(register, newValue);
									}
									else
									{
										throw new Exception($"Use of a undefined local register '{register}'");
									}
								}
								else
								{
									throw new Exception($"Parameter type '{parameter0.Type}' not implemented");
								}
								break;

							case CPUInstructionEnum.SHR:
								parameter0 = instruction.Parameters[0];
								parameter1 = instruction.Parameters[1];

								if (parameter0.Type == CPUParameterTypeEnum.Register)
								{
									register = parameter0.RegisterValue;

									if (localRegisters.ContainsKey(register) && cachedRegisters.ContainsKey(register))
									{
										ILExpression oldValue = localRegisters.GetValueByKey(register);
										ILExpression newValue = new ILOperator(oldValue, ILOperatorEnum.ShiftRight, ParameterToIL(localSegments, localRegisters, parameter1));

										localRegisters.SetValueByKey(register, newValue);
									}
									else
									{
										throw new Exception($"Use of a undefined local register '{register}'");
									}
								}
								else
								{
									throw new Exception($"Parameter type '{parameter0.Type}' not implemented");
								}
								break;

							case CPUInstructionEnum.SAR:
								parameter0 = instruction.Parameters[0];
								parameter1 = instruction.Parameters[1];

								if (parameter0.Type == CPUParameterTypeEnum.Register)
								{
									register = parameter0.RegisterValue;

									if (localRegisters.ContainsKey(register) && cachedRegisters.ContainsKey(register))
									{
										ILExpression oldValue = localRegisters.GetValueByKey(register);
										ILExpression newValue = new ILOperator(oldValue, ILOperatorEnum.ArithmeticShiftRight, ParameterToIL(localSegments, localRegisters, parameter1));

										localRegisters.SetValueByKey(register, newValue);
									}
									else
									{
										throw new Exception($"Use of a undefined local register '{register}'");
									}
								}
								else
								{
									throw new Exception($"Parameter type '{parameter0.Type}' not implemented");
								}
								break;

							case CPUInstructionEnum.SBB:
								parameter0 = instruction.Parameters[0];
								parameter1 = instruction.Parameters[1];

								if (parameter0.Type == CPUParameterTypeEnum.Register)
								{
									register = parameter0.RegisterValue;

									if (localRegisters.ContainsKey(register) && cachedRegisters.ContainsKey(register))
									{
										ILExpression oldValue = localRegisters.GetValueByKey(register);
										ILExpression newValue = new ILOperator(oldValue, ILOperatorEnum.Substract, ParameterToIL(localSegments, localRegisters, parameter1));

										localRegisters.SetValueByKey(register, newValue);
									}
									else
									{
										throw new Exception($"Use of a undefined local register '{register}'");
									}
								}
								else
								{
									throw new Exception($"Parameter type '{parameter0.Type}' not implemented");
								}
								break;

							case CPUInstructionEnum.INC:
								parameter0 = instruction.Parameters[0];
								switch (parameter0.Type)
								{
									case CPUParameterTypeEnum.Register:
										register = parameter0.RegisterValue;

										if (localRegisters.ContainsKey(register))
										{
											node.ILInstructions.Add(new ILUnaryAssignmentOperator(localRegisters.GetValueByKey(register), ILUnaryOperatorEnum.IncrementAfter));
										}
										else
										{
											throw new Exception($"Use of a undefined local register '{register}'");
										}
										break;

									default:
										throw new Exception($"Parameter type '{parameter0.Type}' not implemented");
								}
								break;

							case CPUInstructionEnum.DEC:
								parameter0 = instruction.Parameters[0];
								switch (parameter0.Type)
								{
									case CPUParameterTypeEnum.Register:
										register = parameter0.RegisterValue;

										if (localRegisters.ContainsKey(register))
										{
											node.ILInstructions.Add(new ILUnaryAssignmentOperator(localRegisters.GetValueByKey(register), ILUnaryOperatorEnum.DecrementAfter));
										}
										else
										{
											throw new Exception($"Use of a undefined local register '{register}'");
										}
										break;

									default:
										throw new Exception($"Parameter type '{parameter0.Type}' not implemented");
								}
								break;

							case CPUInstructionEnum.NEG:
								parameter0 = instruction.Parameters[0];
								if (parameter0.Type == CPUParameterTypeEnum.Register)
								{
									register = parameter0.RegisterValue;

									if (localRegisters.ContainsKey(register) && cachedRegisters.ContainsKey(register))
									{
										node.ILInstructions.Add(new ILUnaryAssignmentOperator(localRegisters.GetValueByKey(register), ILUnaryOperatorEnum.Negate));
									}
									else
									{
										throw new Exception($"Use of a undefined local register '{register}'");
									}
								}
								else
								{
									throw new Exception($"Parameter type '{parameter0.Type}' not implemented");
								}
								break;

							case CPUInstructionEnum.NOT:
								parameter0 = instruction.Parameters[0];
								if (parameter0.Type == CPUParameterTypeEnum.Register)
								{
									register = parameter0.RegisterValue;

									if (localRegisters.ContainsKey(register) && cachedRegisters.ContainsKey(register))
									{
										node.ILInstructions.Add(new ILUnaryAssignmentOperator(localRegisters.GetValueByKey(register), ILUnaryOperatorEnum.BitwiseNot));
									}
									else
									{
										throw new Exception($"Use of a undefined local register '{register}'");
									}
								}
								else
								{
									throw new Exception($"Parameter type '{parameter0.Type}' not implemented");
								}
								break;

							case CPUInstructionEnum.NOP:
								// no operation
								break;

							case CPUInstructionEnum.MOV:
								parameter0 = instruction.Parameters[0];

								switch (parameter0.Type)
								{
									case CPUParameterTypeEnum.Register:
										register = parameter0.RegisterValue;
										parentFunction.LocalVariables.Add(parentFunction.LocalVariablePosition, new ILVariable(parentFunction,
											this.parentFunction.ParentSegment.ParentProgram.IntValueType, parentFunction.LocalVariablePosition));
										variable = new ILLocalVariableReference(parentFunction, parentFunction.LocalVariablePosition);

										if (localRegisters.ContainsKey(register))
										{
											localRegisters.SetValueByKey(register, variable);
										}
										else
										{
											localRegisters.Add(register, variable);
										}

										node.ILInstructions.Add(new ILAssignment(variable, ParameterToIL(localSegments, localRegisters, instruction.Parameters[1])));

										parentFunction.LocalVariablePosition += 2;
										break;

									default:
										throw new Exception($"Parameter type '{parameter0.Type}' not implemented");
								}
								break;

							case CPUInstructionEnum.LEA:
								parameter0 = instruction.Parameters[0];
								parameter1 = instruction.Parameters[1];

								if (parameter0.Type == CPUParameterTypeEnum.Register)
								{
									register = parameter0.RegisterValue;
									parentFunction.LocalVariables.Add(parentFunction.LocalVariablePosition, new ILVariable(parentFunction,
										this.parentFunction.ParentSegment.ParentProgram.IntValueType, parentFunction.LocalVariablePosition));
									variable = new ILLocalVariableReference(parentFunction, parentFunction.LocalVariablePosition);

									if (localRegisters.ContainsKey(register))
									{
										localRegisters.SetValueByKey(register, variable);
									}
									else
									{
										localRegisters.Add(register, variable);
									}

									// LEA loads the effective address, not the value at the address
									node.ILInstructions.Add(new ILAssignment(variable, ParameterToIL(localSegments, localRegisters, parameter1)));

									parentFunction.LocalVariablePosition += 2;
								}
								else
								{
									throw new Exception($"Parameter type '{parameter0.Type}' not implemented");
								}
								break;

							case CPUInstructionEnum.MOVZX:
							case CPUInstructionEnum.MOVSX:
								parameter0 = instruction.Parameters[0];

								if (parameter0.Type == CPUParameterTypeEnum.Register)
								{
									register = parameter0.RegisterValue;
									ILValueType destType = parentFunction.ParentSegment.ParentProgram.FromCPUParameterSizeEnum(parameter0.Size);
									parentFunction.LocalVariables.Add(parentFunction.LocalVariablePosition, new ILVariable(parentFunction,
										destType, parentFunction.LocalVariablePosition));
									variable = new ILLocalVariableReference(parentFunction, parentFunction.LocalVariablePosition);

									if (localRegisters.ContainsKey(register))
									{
										localRegisters.SetValueByKey(register, variable);
									}
									else
									{
										localRegisters.Add(register, variable);
									}

									ILExpression sourceExpr = ParameterToIL(localSegments, localRegisters, instruction.Parameters[1]);
									bool isSigned = instruction.InstructionType == CPUInstructionEnum.MOVSX;
									ILExpression castExpr = new ILTypeCast(sourceExpr, destType, isSigned);

									node.ILInstructions.Add(new ILAssignment(variable, castExpr));

									parentFunction.LocalVariablePosition += 2;
								}
								else
								{
									throw new Exception($"Parameter type '{parameter0.Type}' not implemented");
								}
								break;

							case CPUInstructionEnum.XCHG:
								parameter0 = instruction.Parameters[0];
								parameter1 = instruction.Parameters[1];

								if (parameter0.Type == CPUParameterTypeEnum.Register && parameter1.Type == CPUParameterTypeEnum.Register)
								{
									CPURegisterEnum reg0 = parameter0.RegisterValue;
									CPURegisterEnum reg1 = parameter1.RegisterValue;

									if (localRegisters.ContainsKey(reg0) && localRegisters.ContainsKey(reg1))
									{
										ILExpression temp = localRegisters.GetValueByKey(reg0);
										localRegisters.SetValueByKey(reg0, localRegisters.GetValueByKey(reg1));
										localRegisters.SetValueByKey(reg1, temp);
									}
									else
									{
										throw new Exception($"Use of a undefined local register in XCHG");
									}
								}
								else
								{
									throw new Exception($"XCHG parameter type not implemented");
								}
								break;

							case CPUInstructionEnum.CBW:
								// sign-extend AL to AX — treat as MOV AX, (signed)AL
								if (localRegisters.ContainsKey(CPURegisterEnum.AX))
								{
									// CBW is implicit, the value in AX is already tracked
									// In IL terms, this is a sign extension cast — no-op for now
								}
								break;

							case CPUInstructionEnum.CWD:
								// sign-extend AX to DX:AX
								if (localRegisters.ContainsKey(CPURegisterEnum.AX))
								{
									// Set DX based on sign of AX — for IL purposes, track DX as dependent on AX
									ILExpression axValue = localRegisters.GetValueByKey(CPURegisterEnum.AX);
									// DX gets the sign extension (0x0000 or 0xFFFF)
									ILExpression dxValue = new ILOperator(axValue, ILOperatorEnum.ArithmeticShiftRight,
										new ILImmediateValue(parentFunction.ParentSegment.ParentProgram.FromCPUParameterSizeEnum(CPUParameterSizeEnum.UInt16), 15));

									if (localRegisters.ContainsKey(CPURegisterEnum.DX))
									{
										localRegisters.SetValueByKey(CPURegisterEnum.DX, dxValue);
									}
									else
									{
										localRegisters.Add(CPURegisterEnum.DX, dxValue);
									}

									if (!cachedRegisters.ContainsKey(CPURegisterEnum.DX))
									{
										cachedRegisters.Add(CPURegisterEnum.DX, node.ILInstructions.Count);
									}
								}
								break;

							case CPUInstructionEnum.CMP:
								parameter0 = instruction.Parameters[0];
								parameter1 = instruction.Parameters[1];

								// Store the pending comparison operands for use by the next Jcc
								pendingCompareLeft = ParameterToIL(localSegments, localRegisters, parameter0);
								pendingCompareRight = ParameterToIL(localSegments, localRegisters, parameter1);
								pendingCompareType = 0; // CMP
								break;

							case CPUInstructionEnum.TEST:
								parameter0 = instruction.Parameters[0];
								parameter1 = instruction.Parameters[1];

								// Store the pending test operands for use by the next Jcc
								pendingCompareLeft = ParameterToIL(localSegments, localRegisters, parameter0);
								pendingCompareRight = ParameterToIL(localSegments, localRegisters, parameter1);
								pendingCompareType = 1; // TEST
								break;

							case CPUInstructionEnum.Jcc:
								// Conditional jump — translate to ILConditionExpression using pending CMP/TEST
								if (pendingCompareLeft != null && pendingCompareRight != null)
								{
									parameter0 = instruction.Parameters[0]; // condition code
									ILConditionEnum condEnum = ConditionCodeToILCondition((byte)parameter0.Value, pendingCompareType);
									node.ILInstructions.Add(new ILConditionExpression(pendingCompareLeft, condEnum, pendingCompareRight));
									pendingCompareLeft = null;
									pendingCompareRight = null;
								}
								else
								{
									throw new Exception("Jcc without preceding CMP/TEST");
								}
								break;

							case CPUInstructionEnum.JMP:
							case CPUInstructionEnum.JMPF:
								// Unconditional jumps are handled by FlowGraph edges
								break;

							case CPUInstructionEnum.CALL:
								parameter0 = instruction.Parameters[0];

								if (parameter0.Type == CPUParameterTypeEnum.Immediate)
								{
									function = parentFunction.ParentSegment.ParentProgram.FindFunction(0, (ushort)parentFunction.ParentSegment.Segment, (ushort)parameter0.Value);

									if (function != null)
									{
										if ((function.FunctionOptions & ProgramFunctionOptionsEnum.Cdecl) == ProgramFunctionOptionsEnum.Cdecl)
										{
											if (i + 1 >= node.AsmInstructions.Count)
											{
												List<ILExpression> parameterList = [];
												while (localStack.Count > 0)
												{
													parameterList.Add(localStack.Pop());
												}
												node.ILInstructions.Add(new ILFunctionCall(function, parameterList));
											}
											else if ((instruction = node.AsmInstructions[i + 1]).InstructionType == CPUInstructionEnum.ADD &&
												instruction.OperandSize == CPUParameterSizeEnum.UInt16 &&
												instruction.Parameters.Count == 2 &&
												instruction.Parameters[0].Type == CPUParameterTypeEnum.Register &&
												instruction.Parameters[0].Value == (uint)CPURegisterEnum.SP &&
												instruction.Parameters[1].Type == CPUParameterTypeEnum.Immediate)
											{
												if (instruction.Parameters[1].Value == function.ParameterSize)
												{
													List<ILExpression> parameterList = [];
													for (int j = 0; j < function.Parameters.Count; j++)
													{
														parameterList.Add(localStack.Pop());
													}
													node.ILInstructions.Add(new ILFunctionCall(function, parameterList));
													i++;
												}
												else
												{
													throw new Exception($"The function '{function.ParentSegment.Name}.{function.Name}' " +
														$"accepts {function.Parameters.Count} parameters, but {(instruction.Parameters[1].Value / 2)} parameters passed");
												}
											}

											ClearCachedRegister(cachedRegisters, CPURegisterEnum.AX);
											ClearCachedRegister(cachedRegisters, CPURegisterEnum.BX);
											ClearCachedRegister(cachedRegisters, CPURegisterEnum.CX);
											ClearCachedRegister(cachedRegisters, CPURegisterEnum.DX);
										}
										else
										{
											throw new Exception($"Unsupported function call type '{function.FunctionOptions}'");
										}
									}
									else
									{
										throw new Exception($"Can't find the function at {parentFunction.ParentSegment.Segment:x}:0x{parameter0.Value:x}");
									}
								}
								else
								{
									// Indirect near call — target is computed from register or memory
									ILExpression targetExpr = ParameterToIL(localSegments, localRegisters, parameter0);
									List<ILExpression> parameterList = [];

									while (localStack.Count > 0)
									{
										parameterList.Add(localStack.Pop());
									}

									node.ILInstructions.Add(new ILIndirectFunctionCall(targetExpr, parameterList, false));

									ClearCachedRegister(cachedRegisters, CPURegisterEnum.AX);
									ClearCachedRegister(cachedRegisters, CPURegisterEnum.BX);
									ClearCachedRegister(cachedRegisters, CPURegisterEnum.CX);
									ClearCachedRegister(cachedRegisters, CPURegisterEnum.DX);
								}
								break;

							case CPUInstructionEnum.CALLF:
								parameter0 = instruction.Parameters[0];

								if (parameter0.Type == CPUParameterTypeEnum.SegmentOffset)
								{
									function = parentFunction.ParentSegment.ParentProgram.FindFunction(0, parameter0.Segment, (ushort)parameter0.Value);

									if (function != null)
									{
										if ((function.FunctionOptions & ProgramFunctionOptionsEnum.Cdecl) == ProgramFunctionOptionsEnum.Cdecl)
										{
											if (i + 1 >= node.AsmInstructions.Count)
											{
												// this function call is at the end of the function body, no stack adjustment available
												List<ILExpression> parameterList = [];

												while (localStack.Count > 0)
												{
													parameterList.Add(localStack.Pop());
												}

												node.ILInstructions.Add(new ILFunctionCall(function, parameterList));
											}
											else if ((instruction = node.AsmInstructions[i + 1]).InstructionType == CPUInstructionEnum.ADD &&
												instruction.OperandSize == CPUParameterSizeEnum.UInt16 &&
												instruction.Parameters.Count == 2 &&
												instruction.Parameters[0].Type == CPUParameterTypeEnum.Register &&
												instruction.Parameters[0].Value == (uint)CPURegisterEnum.SP &&
												instruction.Parameters[1].Type == CPUParameterTypeEnum.Immediate)
											{
												if (instruction.Parameters[1].Value == function.ParameterSize)
												{
													// normal Cdecl function call
													List<ILExpression> parameterList = [];

													for (int j = 0; j < function.Parameters.Count; j++)
													{
														parameterList.Add(localStack.Pop());
													}

													node.ILInstructions.Add(new ILFunctionCall(function, parameterList));
													i++;
												}
												else
												{
													throw new Exception($"The function '{function.ParentSegment.Name}.{function.Name}' " +
														$"accepts {function.Parameters.Count} parameters, but {(instruction.Parameters[1].Value / 2)} parameters passed");
												}
											}

											// these registers are not preserved when calling the function
											ClearCachedRegister(cachedRegisters, CPURegisterEnum.AX);
											ClearCachedRegister(cachedRegisters, CPURegisterEnum.BX);
											ClearCachedRegister(cachedRegisters, CPURegisterEnum.CX);
											ClearCachedRegister(cachedRegisters, CPURegisterEnum.DX);
										}
										else
										{
											throw new Exception($"Unsupported function call type '{function.FunctionOptions}'");
										}
									}
									else
									{
										throw new Exception($"Can't find the function at 0x{parameter0.Segment:x}:0x{parameter0.Value:x}");
									}
								}
								else
								{
									// Indirect far call — target is computed from memory
									ILExpression targetExpr = ParameterToIL(localSegments, localRegisters, parameter0);
									List<ILExpression> parameterList = [];

									while (localStack.Count > 0)
									{
										parameterList.Add(localStack.Pop());
									}

									node.ILInstructions.Add(new ILIndirectFunctionCall(targetExpr, parameterList, true));

									// Indirect calls clobber all caller-saved registers
									ClearCachedRegister(cachedRegisters, CPURegisterEnum.AX);
									ClearCachedRegister(cachedRegisters, CPURegisterEnum.BX);
									ClearCachedRegister(cachedRegisters, CPURegisterEnum.CX);
									ClearCachedRegister(cachedRegisters, CPURegisterEnum.DX);
								}
								break;

							case CPUInstructionEnum.RET:
							case CPUInstructionEnum.RETF:
								// Return is handled by FlowGraph structure (end node)
								break;

							case CPUInstructionEnum.LOOP:
							case CPUInstructionEnum.LOOPE:
							case CPUInstructionEnum.LOOPNE:
								// LOOP decrements CX and jumps if CX != 0
								// Control flow is handled by FlowGraph edges
								if (localRegisters.ContainsKey(CPURegisterEnum.CX))
								{
									node.ILInstructions.Add(new ILUnaryAssignmentOperator(localRegisters.GetValueByKey(CPURegisterEnum.CX), ILUnaryOperatorEnum.DecrementAfter));
								}
								break;

							case CPUInstructionEnum.PUSH:
								localStack.Push(ParameterToIL(localSegments, localRegisters, instruction.Parameters[0]));
								break;

							case CPUInstructionEnum.POP:
								parameter0 = instruction.Parameters[0];
								if (parameter0.Type == CPUParameterTypeEnum.Register)
								{
									register = parameter0.RegisterValue;

									if (localStack.Count > 0)
									{
										ILExpression poppedValue = localStack.Pop();

										if (localRegisters.ContainsKey(register))
										{
											localRegisters.SetValueByKey(register, poppedValue);
										}
										else
										{
											localRegisters.Add(register, poppedValue);
										}

										if (!cachedRegisters.ContainsKey(register))
										{
											cachedRegisters.Add(register, node.ILInstructions.Count);
										}
									}
									else
									{
										throw new Exception($"POP with empty local stack for register '{register}'");
									}
								}
								else
								{
									throw new Exception($"POP parameter type '{parameter0.Type}' not implemented");
								}
								break;

							case CPUInstructionEnum.PUSHF:
							case CPUInstructionEnum.POPF:
							case CPUInstructionEnum.PUSHA:
							case CPUInstructionEnum.POPA:
								// Flag and register save/restore — skip for IL translation
								break;

							case CPUInstructionEnum.MUL:
								// Unsigned multiply: DX:AX = AX * operand
								parameter0 = instruction.Parameters[0];
								{
									ILExpression mulOperand = ParameterToIL(localSegments, localRegisters, parameter0);

									if (localRegisters.ContainsKey(CPURegisterEnum.AX) && cachedRegisters.ContainsKey(CPURegisterEnum.AX))
									{
										ILExpression axValue = localRegisters.GetValueByKey(CPURegisterEnum.AX);
										ILExpression result = new ILOperator(axValue, ILOperatorEnum.Multiply, mulOperand);

										localRegisters.SetValueByKey(CPURegisterEnum.AX, result);

										// DX gets the high word — approximate as 0 for single-precision results
										if (localRegisters.ContainsKey(CPURegisterEnum.DX))
										{
											localRegisters.SetValueByKey(CPURegisterEnum.DX,
												new ILImmediateValue(parentFunction.ParentSegment.ParentProgram.FromCPUParameterSizeEnum(CPUParameterSizeEnum.UInt16), 0));
										}
									}
									else
									{
										throw new Exception($"Use of a undefined local register 'AX' in MUL");
									}
								}
								break;

							case CPUInstructionEnum.IMUL:
								// Signed multiply
								parameter0 = instruction.Parameters[0];
								if (instruction.Parameters.Count == 1)
								{
									// Single operand: DX:AX = AX * operand
									ILExpression mulOperand = ParameterToIL(localSegments, localRegisters, parameter0);

									if (localRegisters.ContainsKey(CPURegisterEnum.AX) && cachedRegisters.ContainsKey(CPURegisterEnum.AX))
									{
										ILExpression axValue = localRegisters.GetValueByKey(CPURegisterEnum.AX);
										ILExpression result = new ILOperator(axValue, ILOperatorEnum.Multiply, mulOperand);

										localRegisters.SetValueByKey(CPURegisterEnum.AX, result);

										if (localRegisters.ContainsKey(CPURegisterEnum.DX))
										{
											localRegisters.SetValueByKey(CPURegisterEnum.DX,
												new ILImmediateValue(parentFunction.ParentSegment.ParentProgram.FromCPUParameterSizeEnum(CPUParameterSizeEnum.UInt16), 0));
										}
									}
									else
									{
										throw new Exception($"Use of a undefined local register 'AX' in IMUL");
									}
								}
								else if (instruction.Parameters.Count >= 2)
								{
									// Two/three operand form: dest = src * imm
									parameter1 = instruction.Parameters[1];
									if (parameter0.Type == CPUParameterTypeEnum.Register)
									{
										register = parameter0.RegisterValue;
										ILExpression src = ParameterToIL(localSegments, localRegisters, parameter1);
										ILExpression imm = instruction.Parameters.Count == 3
											? ParameterToIL(localSegments, localRegisters, instruction.Parameters[2])
											: src;

										ILExpression result = instruction.Parameters.Count == 3
											? new ILOperator(src, ILOperatorEnum.Multiply, imm)
											: new ILOperator(localRegisters.GetValueByKey(register), ILOperatorEnum.Multiply, src);

										localRegisters.SetValueByKey(register, result);
									}
									else
									{
										throw new Exception($"IMUL parameter type '{parameter0.Type}' not implemented");
									}
								}
								break;

							case CPUInstructionEnum.DIV:
								// Unsigned divide: AX = DX:AX / operand, DX = DX:AX % operand
								parameter0 = instruction.Parameters[0];
								{
									ILExpression divOperand = ParameterToIL(localSegments, localRegisters, parameter0);

									if (localRegisters.ContainsKey(CPURegisterEnum.AX) && cachedRegisters.ContainsKey(CPURegisterEnum.AX))
									{
										ILExpression axValue = localRegisters.GetValueByKey(CPURegisterEnum.AX);
										ILExpression quotient = new ILOperator(axValue, ILOperatorEnum.Divide, divOperand);
										ILExpression remainder = new ILOperator(axValue, ILOperatorEnum.Modulo, divOperand);

										localRegisters.SetValueByKey(CPURegisterEnum.AX, quotient);

										if (localRegisters.ContainsKey(CPURegisterEnum.DX))
										{
											localRegisters.SetValueByKey(CPURegisterEnum.DX, remainder);
										}
										else
										{
											localRegisters.Add(CPURegisterEnum.DX, remainder);
										}

										if (!cachedRegisters.ContainsKey(CPURegisterEnum.DX))
										{
											cachedRegisters.Add(CPURegisterEnum.DX, node.ILInstructions.Count);
										}
									}
									else
									{
										throw new Exception($"Use of a undefined local register 'AX' in DIV");
									}
								}
								break;

							case CPUInstructionEnum.IDIV:
								// Signed divide: AX = DX:AX / operand, DX = DX:AX % operand
								parameter0 = instruction.Parameters[0];
								{
									ILExpression divOperand = ParameterToIL(localSegments, localRegisters, parameter0);

									if (localRegisters.ContainsKey(CPURegisterEnum.AX) && cachedRegisters.ContainsKey(CPURegisterEnum.AX))
									{
										ILExpression axValue = localRegisters.GetValueByKey(CPURegisterEnum.AX);
										ILExpression quotient = new ILOperator(axValue, ILOperatorEnum.Divide, divOperand);
										ILExpression remainder = new ILOperator(axValue, ILOperatorEnum.Modulo, divOperand);

										localRegisters.SetValueByKey(CPURegisterEnum.AX, quotient);

										if (localRegisters.ContainsKey(CPURegisterEnum.DX))
										{
											localRegisters.SetValueByKey(CPURegisterEnum.DX, remainder);
										}
										else
										{
											localRegisters.Add(CPURegisterEnum.DX, remainder);
										}

										if (!cachedRegisters.ContainsKey(CPURegisterEnum.DX))
										{
											cachedRegisters.Add(CPURegisterEnum.DX, node.ILInstructions.Count);
										}
									}
									else
									{
										throw new Exception($"Use of a undefined local register 'AX' in IDIV");
									}
								}
								break;

							case CPUInstructionEnum.CLC:
							case CPUInstructionEnum.STC:
							case CPUInstructionEnum.CLD:
							case CPUInstructionEnum.STD:
							case CPUInstructionEnum.CLI:
							case CPUInstructionEnum.STI:
								// Flag manipulation — skip for IL translation
								break;

							case CPUInstructionEnum.LDS:
							case CPUInstructionEnum.LES:
								// Load far pointer: reg = [mem], segment = [mem+2]
								parameter0 = instruction.Parameters[0];
								parameter1 = instruction.Parameters[1];

								if (parameter0.Type == CPUParameterTypeEnum.Register)
								{
									register = parameter0.RegisterValue;
									parentFunction.LocalVariables.Add(parentFunction.LocalVariablePosition, new ILVariable(parentFunction,
										this.parentFunction.ParentSegment.ParentProgram.IntValueType, parentFunction.LocalVariablePosition));
									variable = new ILLocalVariableReference(parentFunction, parentFunction.LocalVariablePosition);

									if (localRegisters.ContainsKey(register))
									{
										localRegisters.SetValueByKey(register, variable);
									}
									else
									{
										localRegisters.Add(register, variable);
									}

									node.ILInstructions.Add(new ILAssignment(variable, ParameterToIL(localSegments, localRegisters, parameter1)));

									parentFunction.LocalVariablePosition += 2;
								}
								else
								{
									throw new Exception($"LDS/LES parameter type '{parameter0.Type}' not implemented");
								}
								break;

							case CPUInstructionEnum.SAHF:
							case CPUInstructionEnum.LAHF:
								// Load/store flags from/to AH — skip for IL translation
								break;

							default:
								throw new Exception($"Don't know how to translate '{instruction}'");
						}
					}

					// if any cached register is defined, define it

					#endregion

					#region Process child nodes
					for (int i = 0; i < node.ChildNodes.Count; i++)
					{
						FlowGraphNode childNode = node.ChildNodes[i];

						if (!childNode.TranslatedToIL)
						{
							translated &= TranslateNodeToIL(childNode, localRegisters, localSegments, localStack);
						}
					}
					#endregion
				}
			}

			return translated;
		}

		private void ClearCachedRegister(BDictionary<CPURegisterEnum, int> cache, CPURegisterEnum register)
		{
			if (cache.ContainsKey(register))
			{
				cache.RemoveByKey(register);
			}
		}

		/// <summary>
		/// Maps x86 condition codes (from Jcc instructions) to IL condition enums.
		/// </summary>
		/// <param name="conditionCode">The condition code from the Jcc instruction (0x0-0xF).</param>
		/// <param name="compareType">0 for CMP, 1 for TEST.</param>
		private static ILConditionEnum ConditionCodeToILCondition(byte conditionCode, int compareType)
		{
			// x86 condition codes (low nibble of Jcc opcode):
			// 0x0 = O,  0x1 = NO, 0x2 = B/C,  0x3 = AE/NC
			// 0x4 = E/Z, 0x5 = NE/NZ, 0x6 = BE, 0x7 = A
			// 0x8 = S, 0x9 = NS, 0xA = P, 0xB = NP
			// 0xC = L, 0xD = GE, 0xE = LE, 0xF = G
			switch (conditionCode & 0x0F)
			{
				case 0x0: return ILConditionEnum.Overflow;
				case 0x1: return ILConditionEnum.NotOverflow;
				case 0x2: return ILConditionEnum.UnsignedLessThan;          // JB/JC
				case 0x3: return ILConditionEnum.UnsignedGreaterThanOrEqual; // JAE/JNC
				case 0x4: return ILConditionEnum.Equal;                      // JE/JZ
				case 0x5: return ILConditionEnum.NotEqual;                   // JNE/JNZ
				case 0x6: return ILConditionEnum.UnsignedLessThanOrEqual;    // JBE
				case 0x7: return ILConditionEnum.UnsignedGreaterThan;        // JA
				case 0x8: return ILConditionEnum.Sign;                       // JS
				case 0x9: return ILConditionEnum.NotSign;                    // JNS
				// 0xA (JP) and 0xB (JNP) — parity, rare in application code
				case 0xC: return ILConditionEnum.LessThan;                   // JL
				case 0xD: return ILConditionEnum.GreaterThanOrEqual;          // JGE
				case 0xE: return ILConditionEnum.LessThanOrEqual;            // JLE
				case 0xF: return ILConditionEnum.GreaterThan;                // JG
				default:
					throw new Exception($"Unknown condition code 0x{conditionCode:X2}");
			}
		}

		private static ILExpression GetRegisterExpr(BDictionary<CPURegisterEnum, ILExpression> localRegisters, CPURegisterEnum reg)
		{
			if (localRegisters.ContainsKey(reg))
			{
				return localRegisters.GetValueByKey(reg);
			}

			throw new Exception($"Use of an undefined local register '{reg}' in memory addressing");
		}

		private ILExpression ResolveMemoryAddress(ProgramSegment? segment,
			BDictionary<CPURegisterEnum, ILExpression> localRegisters, ILValueType memType,
			ILExpression? baseExpr, ILExpression? indexExpr, int displacement, bool hasDisplacement)
		{
			if (segment != null)
			{
				return new ILMemoryReference(segment, memType, baseExpr, indexExpr, displacement, hasDisplacement);
			}

			// SS segment (stack-relative) — build an address expression
			// This would typically be a local variable or parameter access via BP
			ILExpression addr = baseExpr!;
			if (indexExpr != null)
			{
				addr = new ILOperator(addr, ILOperatorEnum.Add, indexExpr);
			}
			if (hasDisplacement && displacement != 0)
			{
				addr = new ILOperator(addr, ILOperatorEnum.Add,
					new ILImmediateValue(this.parentFunction.ParentSegment.ParentProgram.FromCPUParameterSizeEnum(CPUParameterSizeEnum.UInt16), (uint)displacement));
			}
			return addr;
		}

		private ILExpression ParameterToIL(BDictionary<CPUSegmentRegisterEnum, uint> localSegments,
			BDictionary<CPURegisterEnum, ILExpression> localRegisters, CPUParameter parameter)
		{
			ProgramFunction parentFunction = this.parentFunction;

			switch (parameter.Type)
			{
				case CPUParameterTypeEnum.Immediate:
					switch (parameter.Size)
					{
						case CPUParameterSizeEnum.UInt8:
							return new ILImmediateValue(parentFunction.ParentSegment.ParentProgram.FromCPUParameterSizeEnum(parameter.Size), parameter.Value);

						case CPUParameterSizeEnum.UInt16:
							return new ILImmediateValue(parentFunction.ParentSegment.ParentProgram.FromCPUParameterSizeEnum(parameter.Size), parameter.Value);

						case CPUParameterSizeEnum.UInt32:
							return new ILImmediateValue(parentFunction.ParentSegment.ParentProgram.FromCPUParameterSizeEnum(parameter.Size), parameter.Value);

						default:
							throw new Exception($"Parameter size {parameter.Size} not implemented");
					}

				case CPUParameterTypeEnum.Register:
					CPURegisterEnum register = parameter.RegisterValue;
					if (localRegisters.ContainsKey(register))
					{
						return localRegisters.GetValueByKey(register);
					}
					else
					{
						throw new Exception($"Use of a undefined variable '{register}'");
					}

				case CPUParameterTypeEnum.LocalParameter:
					return new ILLocalParameterReference(parentFunction, (int)parameter.Displacement);

				case CPUParameterTypeEnum.LocalVariable:
					return new ILLocalVariableReference(parentFunction, (int)parameter.Displacement);

				case CPUParameterTypeEnum.MemoryAddress:
				{
					ILValueType memType = parentFunction.ParentSegment.ParentProgram.FromCPUParameterSizeEnum(parameter.Size);

					if (parameter.DataSegment == CPUSegmentRegisterEnum.SS)
					{
						// SS-relative addressing — BP-based modes are local variables/parameters
						// Non-BP modes on SS are unusual and not supported
						switch (parameter.Value)
						{
							case 0: // [BX + SI]
							case 1: // [BX + DI]
							case 4: // [SI]
							case 5: // [DI]
							case 6: // [{0}]
							case 7: // [BX]
							case 8: // [{0} + BX + SI]
							case 9: // [{0} + BX + DI]
							case 12: // [{0} + SI]
							case 13: // [{0} + DI]
							case 15: // [{0} + BX]
							case 16: // [{0} + BX + SI]
							case 17: // [{0} + BX + DI]
							case 20: // [{0} + SI]
							case 21: // [{0} + DI]
							case 23: // [{0} + BX]
								throw new Exception($"The addressing type {parameter} is not supported on segment {parameter.DataSegment}");

							case 2: // [BP + SI]
							case 3: // [BP + DI]
								return ResolveMemoryAddress(null, localRegisters, memType,
									GetRegisterExpr(localRegisters, CPURegisterEnum.BP),
									GetRegisterExpr(localRegisters, (parameter.Value & 1) == 0 ? CPURegisterEnum.SI : CPURegisterEnum.DI),
									0, false);

							case 10: // [BP + disp8 + SI]
							case 11: // [BP + disp8 + DI]
								return ResolveMemoryAddress(null, localRegisters, memType,
									GetRegisterExpr(localRegisters, CPURegisterEnum.BP),
									GetRegisterExpr(localRegisters, (parameter.Value & 1) == 0 ? CPURegisterEnum.SI : CPURegisterEnum.DI),
									parameter.Displacement, true);

							case 14: // [BP + disp8]
								return ResolveMemoryAddress(null, localRegisters, memType,
									GetRegisterExpr(localRegisters, CPURegisterEnum.BP), null,
									parameter.Displacement, true);

							case 18: // [BP + disp16 + SI]
							case 19: // [BP + disp16 + DI]
								return ResolveMemoryAddress(null, localRegisters, memType,
									GetRegisterExpr(localRegisters, CPURegisterEnum.BP),
									GetRegisterExpr(localRegisters, (parameter.Value & 1) == 0 ? CPURegisterEnum.SI : CPURegisterEnum.DI),
									parameter.Displacement, true);

							case 22: // [BP + disp16]
								return ResolveMemoryAddress(null, localRegisters, memType,
									GetRegisterExpr(localRegisters, CPURegisterEnum.BP), null,
									parameter.Displacement, true);

							default:
								throw new Exception($"Unknown SS addressing mode {parameter.Value}");
						}
					}
					else
					{
						if (!localSegments.ContainsKey(parameter.DataSegment))
						{
							throw new Exception($"The segment '{parameter.DataSegment}' is not defined");
						}

						ProgramSegment segment = parentFunction.ParentSegment.ParentProgram.FindOrCreateSegment(
							parentFunction.ParentSegment.CPUOverlay, (ushort)localSegments.GetValueByKey(parameter.DataSegment));

						// BP-relative modes don't make sense on data segments
						switch (parameter.Value)
						{
							case 2: // [BP + SI]
							case 3: // [BP + DI]
							case 10: // [BP + disp8 + SI]
							case 11: // [BP + disp8 + DI]
							case 14: // [BP + disp8]
							case 18: // [BP + disp16 + SI]
							case 19: // [BP + disp16 + DI]
							case 22: // [BP + disp16]
								throw new Exception($"The addressing type {parameter} is not supported on segment {parameter.DataSegment}");

							case 6: // [disp16] — direct address
								segment.GetOrDefineGlobalVariable(parameter.Size, parameter.Displacement);
								return new ILGlobalVariableReference(segment, parameter.Displacement);

							case 0: // [BX + SI]
							case 1: // [BX + DI]
								return ResolveMemoryAddress(segment, localRegisters, memType,
									GetRegisterExpr(localRegisters, CPURegisterEnum.BX),
									GetRegisterExpr(localRegisters, (parameter.Value & 1) == 0 ? CPURegisterEnum.SI : CPURegisterEnum.DI),
									0, false);

							case 4: // [SI]
								return ResolveMemoryAddress(segment, localRegisters, memType,
									GetRegisterExpr(localRegisters, CPURegisterEnum.SI), null, 0, false);

							case 5: // [DI]
								return ResolveMemoryAddress(segment, localRegisters, memType,
									GetRegisterExpr(localRegisters, CPURegisterEnum.DI), null, 0, false);

							case 7: // [BX]
								return ResolveMemoryAddress(segment, localRegisters, memType,
									GetRegisterExpr(localRegisters, CPURegisterEnum.BX), null, 0, false);

							case 8: // [disp8 + BX + SI]
							case 9: // [disp8 + BX + DI]
								return ResolveMemoryAddress(segment, localRegisters, memType,
									GetRegisterExpr(localRegisters, CPURegisterEnum.BX),
									GetRegisterExpr(localRegisters, (parameter.Value & 1) == 0 ? CPURegisterEnum.SI : CPURegisterEnum.DI),
									parameter.Displacement, true);

							case 12: // [disp8 + SI]
								return ResolveMemoryAddress(segment, localRegisters, memType,
									GetRegisterExpr(localRegisters, CPURegisterEnum.SI), null, parameter.Displacement, true);

							case 13: // [disp8 + DI]
								return ResolveMemoryAddress(segment, localRegisters, memType,
									GetRegisterExpr(localRegisters, CPURegisterEnum.DI), null, parameter.Displacement, true);

							case 15: // [disp8 + BX]
								return ResolveMemoryAddress(segment, localRegisters, memType,
									GetRegisterExpr(localRegisters, CPURegisterEnum.BX), null, parameter.Displacement, true);

							case 16: // [disp16 + BX + SI]
							case 17: // [disp16 + BX + DI]
								return ResolveMemoryAddress(segment, localRegisters, memType,
									GetRegisterExpr(localRegisters, CPURegisterEnum.BX),
									GetRegisterExpr(localRegisters, (parameter.Value & 1) == 0 ? CPURegisterEnum.SI : CPURegisterEnum.DI),
									parameter.Displacement, true);

							case 20: // [disp16 + SI]
								return ResolveMemoryAddress(segment, localRegisters, memType,
									GetRegisterExpr(localRegisters, CPURegisterEnum.SI), null, parameter.Displacement, true);

							case 21: // [disp16 + DI]
								return ResolveMemoryAddress(segment, localRegisters, memType,
									GetRegisterExpr(localRegisters, CPURegisterEnum.DI), null, parameter.Displacement, true);

							case 23: // [disp16 + BX]
								return ResolveMemoryAddress(segment, localRegisters, memType,
									GetRegisterExpr(localRegisters, CPURegisterEnum.BX), null, parameter.Displacement, true);

							default:
								throw new Exception($"Unknown addressing mode {parameter.Value}");
						}
					}
				}
				default:
					throw new Exception($"Parameter type '{parameter.Type}' not implemented");
			}
		}

		private void DetermineBasicLanguageConstructionBlocks()
		{
			FlowGraphNode currentNode = this.startNode!;
			FlowGraphNode blockStartNode = this.endNodes[0].Value;
			FlowGraphNode blockEndNode = this.endNodes[0].Value;
			List<FlowGraphNode> nodes = [];
			Queue<FlowGraphNode> branches = [];

			// find the first node that is referenced
			while (currentNode.NodeType != FlowGraphNodeTypeEnum.End)
			{
				if (currentNode.ReferenceNodes.Count > 0)
				{
					blockStartNode = currentNode;
					break;
				}
				else
				{
					for (int i = 0; i < currentNode.ChildNodes.Count; i++)
					{
						branches.Enqueue(currentNode.ChildNodes[i]);
					}

					currentNode = branches.Dequeue();
				}
			}

			// find the smallest subset of nodes between referenced node and child nodes
		}

		private FlowGraphNode CreateOrFindNode(uint address, FlowGraphNodeTypeEnum blockType, Queue<FlowGraphNode> unprocessedNodes, bool queue)
		{
			FlowGraphNode node;

			if (this.nodes.ContainsKey(address))
			{
				node = this.nodes.GetValueByKey(address);
			}
			else
			{
				node = new(this, blockType, address);

				this.nodes.Add(address, node);
				if (queue)
				{
					unprocessedNodes.Enqueue(node);
				}
			}

			return node;
		}

		public static bool TestLocal(FlowGraphLocalEnum flags, FlowGraphLocalEnum flag)
		{
			return (flags & flag) == flag;
		}

		public static FlowGraphLocalEnum GetRequiredLocals(CPUInstruction instruction)
		{
			FlowGraphLocalEnum required = FlowGraphLocalEnum.None;

			switch (instruction.InstructionType)
			{
				// Arithmetic instructions
				case CPUInstructionEnum.ADC:
				case CPUInstructionEnum.SBB:
					required |= FlowGraphLocalEnum.CFlag;
					required |= SourceParameterToRequiredLocals(instruction.Parameters[0]);
					required |= SourceParameterToRequiredLocals(instruction.Parameters[1]);
					break;

				case CPUInstructionEnum.ADD:
				case CPUInstructionEnum.SUB:
				case CPUInstructionEnum.AND:
				case CPUInstructionEnum.OR:
				case CPUInstructionEnum.XOR:
					required |= SourceParameterToRequiredLocals(instruction.Parameters[0]);
					required |= SourceParameterToRequiredLocals(instruction.Parameters[1]);
					break;

				case CPUInstructionEnum.DEC:
				case CPUInstructionEnum.INC:
				case CPUInstructionEnum.NEG:
				case CPUInstructionEnum.NOT:
					required |= SourceParameterToRequiredLocals(instruction.Parameters[0]);
					break;

				case CPUInstructionEnum.DAS:
					required |= FlowGraphLocalEnum.CFlag;
					required |= FlowGraphLocalEnum.AL;
					break;

				// Multiply and divide instructions
				case CPUInstructionEnum.MUL:
					required |= SourceParameterToRequiredLocals(instruction.Parameters[1]);
					required |= SourceParameterToRequiredLocals(instruction.Parameters[2]);
					break;

				case CPUInstructionEnum.IMUL:
					required |= SourceParameterToRequiredLocals(instruction.Parameters[1]);
					required |= SourceParameterToRequiredLocals(instruction.Parameters[2]);
					break;

				case CPUInstructionEnum.DIV:
				case CPUInstructionEnum.IDIV:
					if (instruction.OperandSize == CPUParameterSizeEnum.UInt8)
					{
						required |= FlowGraphLocalEnum.AX;
						required |= SourceParameterToRequiredLocals(instruction.Parameters[0]);
					}
					else
					{
						required |= FlowGraphLocalEnum.AX;
						required |= FlowGraphLocalEnum.DX;
						required |= SourceParameterToRequiredLocals(instruction.Parameters[0]);
					}
					break;

				// Shifting instructions
				case CPUInstructionEnum.RCR:
				case CPUInstructionEnum.RCL:
					required |= FlowGraphLocalEnum.CFlag;
					required |= SourceParameterToRequiredLocals(instruction.Parameters[0]);
					required |= SourceParameterToRequiredLocals(instruction.Parameters[1]);
					break;

				case CPUInstructionEnum.SAR:
				case CPUInstructionEnum.SHL:
				case CPUInstructionEnum.SHR:
				case CPUInstructionEnum.ROL:
				case CPUInstructionEnum.ROR:
					required |= SourceParameterToRequiredLocals(instruction.Parameters[0]);
					required |= SourceParameterToRequiredLocals(instruction.Parameters[1]);
					break;

				// Converting instructions
				case CPUInstructionEnum.CBW:
					required |= FlowGraphLocalEnum.AL;
					break;

				case CPUInstructionEnum.CWD:
					required |= FlowGraphLocalEnum.AX;
					break;

				// Compare and test instructions
				case CPUInstructionEnum.CMP:
				case CPUInstructionEnum.TEST:
					required |= SourceParameterToRequiredLocals(instruction.Parameters[0]);
					required |= SourceParameterToRequiredLocals(instruction.Parameters[1]);
					break;

				// Load segment and offset instructions
				case CPUInstructionEnum.LDS:
					required |= SourceParameterToRequiredLocals(instruction.Parameters[1]);
					break;

				case CPUInstructionEnum.LES:
					required |= SourceParameterToRequiredLocals(instruction.Parameters[1]);
					break;

				case CPUInstructionEnum.LEA:
					required |= SourceParameterToRequiredLocals(instruction.Parameters[1]);
					break;

				// String instructions
				case CPUInstructionEnum.LODS:
					required |= FlowGraphLocalEnum.DFlag;
					required |= FlowGraphLocalEnum.SI;
					required |= CPUSegmentRegisterToLocal(instruction.DefaultDataSegment);
					break;

				case CPUInstructionEnum.MOVS:
					required |= FlowGraphLocalEnum.DFlag;
					required |= FlowGraphLocalEnum.DI;
					required |= FlowGraphLocalEnum.ES;
					required |= FlowGraphLocalEnum.SI;
					required |= CPUSegmentRegisterToLocal(instruction.DefaultDataSegment);
					break;

				case CPUInstructionEnum.OUTS:
					required |= FlowGraphLocalEnum.DFlag;
					required |= FlowGraphLocalEnum.SI;
					required |= CPUSegmentRegisterToLocal(instruction.DefaultDataSegment);
					required |= FlowGraphLocalEnum.DX;
					break;

				case CPUInstructionEnum.STOS:
					required |= FlowGraphLocalEnum.DFlag;
					if (instruction.OperandSize == CPUParameterSizeEnum.UInt8)
					{
						required |= FlowGraphLocalEnum.DI;
						required |= FlowGraphLocalEnum.ES;
						required |= FlowGraphLocalEnum.AL;
					}
					else
					{
						required |= FlowGraphLocalEnum.DI;
						required |= FlowGraphLocalEnum.ES;
						required |= FlowGraphLocalEnum.AX;
					}
					break;

				// Data move and convert instructions
				case CPUInstructionEnum.MOV:
					required |= DestinationParameterToRequiredLocals(instruction.Parameters[0]);
					required |= SourceParameterToRequiredLocals(instruction.Parameters[1]);
					break;

				// Ignored instructions
				case CPUInstructionEnum.WAIT:
				case CPUInstructionEnum.NOP:
					break;

				// BP based stack frame instructions
				case CPUInstructionEnum.ENTER:
					break;

				case CPUInstructionEnum.LEAVE:
					break;

				// Stack instructions
				case CPUInstructionEnum.PUSH:
					required |= SourceParameterToRequiredLocals(instruction.Parameters[0]);
					break;

				case CPUInstructionEnum.PUSHA:
					break;

				case CPUInstructionEnum.PUSHF:
					break;

				case CPUInstructionEnum.POP:
					required |= DestinationParameterToRequiredLocals(instruction.Parameters[0]);
					break;

				case CPUInstructionEnum.POPA:
					break;

				case CPUInstructionEnum.POPF:
					break;

				// Flag instructions
				case CPUInstructionEnum.CLD:
					break;

				case CPUInstructionEnum.STD:
					break;

				case CPUInstructionEnum.CLC:
					break;

				case CPUInstructionEnum.STC:
					break;

				case CPUInstructionEnum.CMC:
					required |= FlowGraphLocalEnum.CFlag;
					break;

				case CPUInstructionEnum.CLI:
					break;

				case CPUInstructionEnum.STI:
					break;

				// Exchange data instruction
				case CPUInstructionEnum.XCHG:
					required |= SourceParameterToRequiredLocals(instruction.Parameters[0]);
					required |= SourceParameterToRequiredLocals(instruction.Parameters[1]);
					break;

				// Input and output port instructions
				case CPUInstructionEnum.IN:
					required |= SourceParameterToRequiredLocals(instruction.Parameters[1]);
					break;

				case CPUInstructionEnum.OUT:
					required |= SourceParameterToRequiredLocals(instruction.Parameters[0]);
					required |= SourceParameterToRequiredLocals(instruction.Parameters[1]);
					break;

				// Special syntetic functions
				case CPUInstructionEnum.WordsToDword:
					required |= SourceParameterToRequiredLocals(instruction.Parameters[1]);
					required |= SourceParameterToRequiredLocals(instruction.Parameters[2]);
					break;

				// Flow control instructions
				case CPUInstructionEnum.Jcc:
					required |= SourceParameterToRequiredLocals(instruction.Parameters[0]);
					break;

				case CPUInstructionEnum.JCXZ:
					required |= FlowGraphLocalEnum.CX;
					break;

				case CPUInstructionEnum.LOOP:
					required |= FlowGraphLocalEnum.CX;
					break;

				case CPUInstructionEnum.JMP:
					required |= SourceParameterToRequiredLocals(instruction.Parameters[0]);
					break;

				case CPUInstructionEnum.JMPF:
					required |= SourceParameterToRequiredLocals(instruction.Parameters[0]);
					break;

				case CPUInstructionEnum.SWITCH:
					required |= SourceParameterToRequiredLocals(instruction.Parameters[0]);
					break;

				case CPUInstructionEnum.CALL:
					required |= SourceParameterToRequiredLocals(instruction.Parameters[0]);
					break;

				case CPUInstructionEnum.CALLF:
					required |= SourceParameterToRequiredLocals(instruction.Parameters[0]);
					break;

				case CPUInstructionEnum.CallOverlay:
					break;

				case CPUInstructionEnum.INT:
					required |= SourceParameterToRequiredLocals(instruction.Parameters[0]);
					break;

				case CPUInstructionEnum.RET:
				case CPUInstructionEnum.RETF:
					if (instruction.Parameters.Count > 0)
					{
						required |= SourceParameterToRequiredLocals(instruction.Parameters[0]);
					}
					break;

				case CPUInstructionEnum.IRET:
					break;

				default:
					Console.WriteLine($"Unexpected instruction type '{instruction.InstructionType}'");
					break;
			}

			return required;
		}

		public static FlowGraphLocalEnum GetDefinedLocals(CPUInstruction instruction)
		{
			FlowGraphLocalEnum defined = FlowGraphLocalEnum.None;

			switch (instruction.InstructionType)
			{
				// Arithmetic instructions
				case CPUInstructionEnum.ADC:
				case CPUInstructionEnum.SBB:
					break;

				case CPUInstructionEnum.ADD:
				case CPUInstructionEnum.SUB:
				case CPUInstructionEnum.AND:
				case CPUInstructionEnum.OR:
				case CPUInstructionEnum.XOR:
					break;

				case CPUInstructionEnum.DEC:
				case CPUInstructionEnum.INC:
				case CPUInstructionEnum.NEG:
				case CPUInstructionEnum.NOT:
					break;

				case CPUInstructionEnum.DAS:
					break;

				// Multiply and divide instructions
				case CPUInstructionEnum.MUL:
					break;

				case CPUInstructionEnum.IMUL:
					break;

				case CPUInstructionEnum.DIV:
				case CPUInstructionEnum.IDIV:
					break;

				// Shifting instructions
				case CPUInstructionEnum.RCR:
				case CPUInstructionEnum.RCL:
					break;

				case CPUInstructionEnum.SAR:
				case CPUInstructionEnum.SHL:
				case CPUInstructionEnum.SHR:
				case CPUInstructionEnum.ROL:
				case CPUInstructionEnum.ROR:
					break;

				// Converting instructions
				case CPUInstructionEnum.CBW:
					defined |= FlowGraphLocalEnum.AH;
					break;

				case CPUInstructionEnum.CWD:
					defined |= FlowGraphLocalEnum.DX;
					break;

				// Compare and test instructions
				case CPUInstructionEnum.CMP:
				case CPUInstructionEnum.TEST:
					break;

				// Load segment and offset instructions
				case CPUInstructionEnum.LDS:
					defined |= FlowGraphLocalEnum.DS;
					defined |= DestinationParameterToDefinedLocals(instruction.Parameters[0]);
					break;

				case CPUInstructionEnum.LES:
					defined |= FlowGraphLocalEnum.ES;
					defined |= DestinationParameterToDefinedLocals(instruction.Parameters[0]);
					break;

				case CPUInstructionEnum.LEA:
					defined |= DestinationParameterToDefinedLocals(instruction.Parameters[0]);
					break;

				// String instructions
				case CPUInstructionEnum.LODS:
					switch (instruction.OperandSize)
					{
						case CPUParameterSizeEnum.UInt8:
							defined |= FlowGraphLocalEnum.AL;
							break;

						case CPUParameterSizeEnum.UInt16:
						case CPUParameterSizeEnum.UInt32:
							defined |= FlowGraphLocalEnum.AX;
							break;
					}
					break;

				case CPUInstructionEnum.MOVS:
				case CPUInstructionEnum.OUTS:
				case CPUInstructionEnum.STOS:
					break;

				// Data move and convert instructions
				case CPUInstructionEnum.MOV:
					defined |= DestinationParameterToDefinedLocals(instruction.Parameters[0]);
					break;

				// Ignored instructions
				case CPUInstructionEnum.WAIT:
				case CPUInstructionEnum.NOP:
					break;

				// BP based stack frame instructions
				case CPUInstructionEnum.ENTER:
				case CPUInstructionEnum.LEAVE:
					break;

				// Stack instructions
				case CPUInstructionEnum.PUSH:
					break;

				case CPUInstructionEnum.PUSHA:
					break;

				case CPUInstructionEnum.PUSHF:
					break;

				case CPUInstructionEnum.POP:
					defined |= DestinationParameterToDefinedLocals(instruction.Parameters[0]);
					break;

				case CPUInstructionEnum.POPA:
					break;

				case CPUInstructionEnum.POPF:
					break;

				// Flag instructions
				case CPUInstructionEnum.CLD:
					break;

				case CPUInstructionEnum.STD:
					break;

				case CPUInstructionEnum.CLC:
					break;

				case CPUInstructionEnum.STC:
					break;

				case CPUInstructionEnum.CMC:
					break;

				case CPUInstructionEnum.CLI:
					break;

				case CPUInstructionEnum.STI:
					break;

				// Exchange data instruction
				case CPUInstructionEnum.XCHG:
					break;

				// Input and output port instructions
				case CPUInstructionEnum.IN:
					switch (instruction.OperandSize)
					{
						case CPUParameterSizeEnum.UInt8:
							defined |= FlowGraphLocalEnum.AL;
							break;

						case CPUParameterSizeEnum.UInt16:
						case CPUParameterSizeEnum.UInt32:
							defined |= FlowGraphLocalEnum.AX;
							break;
					}
					break;

				case CPUInstructionEnum.OUT:
					break;

				// Special syntetic functions
				case CPUInstructionEnum.WordsToDword:
					defined |= DestinationParameterToDefinedLocals(instruction.Parameters[0]);
					break;

				// Flow control instructions
				case CPUInstructionEnum.Jcc:
					break;

				case CPUInstructionEnum.JCXZ:
					break;

				case CPUInstructionEnum.LOOP:
					break;

				case CPUInstructionEnum.JMP:
					break;

				case CPUInstructionEnum.JMPF:
					break;

				case CPUInstructionEnum.SWITCH:
					break;

				case CPUInstructionEnum.CALL:
				case CPUInstructionEnum.CALLF:
				case CPUInstructionEnum.CallOverlay:
					defined |= FlowGraphLocalEnum.AX;
					defined |= FlowGraphLocalEnum.DX;
					break;

				case CPUInstructionEnum.INT:
					break;

				case CPUInstructionEnum.RET:
				case CPUInstructionEnum.RETF:
					break;

				case CPUInstructionEnum.IRET:
					break;

				default:
					Console.WriteLine($"Unexpected instruction type '{instruction.InstructionType}'");
					break;
			}

			// and now define flags
			defined |= CPUFlagsToLocals(instruction.ClearedFlags | instruction.ModifiedFlags | instruction.SetFlags | instruction.UndefinedFlags);

			return defined;
		}

		private static FlowGraphLocalEnum SourceParameterToRequiredLocals(CPUParameter parameter)
		{
			FlowGraphLocalEnum locals = FlowGraphLocalEnum.None;

			if (parameter.Size == CPUParameterSizeEnum.UInt32 &&
				parameter.Type != CPUParameterTypeEnum.LocalParameter &&
				parameter.Type != CPUParameterTypeEnum.LocalParameterWithSI &&
				parameter.Type != CPUParameterTypeEnum.LocalParameterWithDI &&
				parameter.Type != CPUParameterTypeEnum.LocalVariable)
			{
				throw new Exception("x32 addressing mode not yet implemented");
			}

			switch (parameter.Type)
			{
				case CPUParameterTypeEnum.Immediate:
				case CPUParameterTypeEnum.SegmentOffset:
					break;

				case CPUParameterTypeEnum.Register:
					locals |= CPURegisterToLocal(parameter.RegisterValue);
					break;

				case CPUParameterTypeEnum.SegmentRegister:
					locals |= CPUSegmentRegisterToLocal(parameter.SegmentRegisterValue);
					break;

				case CPUParameterTypeEnum.Condition:
					switch ((CPUJumpConditionEnum)parameter.Value)
					{
						case CPUJumpConditionEnum.O:
						case CPUJumpConditionEnum.NO:
							locals |= FlowGraphLocalEnum.OFlag;
							break;

						case CPUJumpConditionEnum.S:
						case CPUJumpConditionEnum.NS:
							locals |= FlowGraphLocalEnum.SFlag;
							break;

						case CPUJumpConditionEnum.A:
						case CPUJumpConditionEnum.BE:
							locals |= FlowGraphLocalEnum.ZFlag | FlowGraphLocalEnum.CFlag;
							break;

						case CPUJumpConditionEnum.AE:
						case CPUJumpConditionEnum.B:
							locals |= FlowGraphLocalEnum.CFlag;
							break;

						case CPUJumpConditionEnum.E:
							locals |= FlowGraphLocalEnum.ZFlag;
							break;

						case CPUJumpConditionEnum.NE:
							locals |= FlowGraphLocalEnum.ZFlag;
							break;

						case CPUJumpConditionEnum.G:
						case CPUJumpConditionEnum.LE:
							locals |= FlowGraphLocalEnum.ZFlag | FlowGraphLocalEnum.SFlag | FlowGraphLocalEnum.OFlag;
							break;

						case CPUJumpConditionEnum.GE:
						case CPUJumpConditionEnum.L:
							locals |= FlowGraphLocalEnum.SFlag | FlowGraphLocalEnum.OFlag;
							break;


						default:
							Console.WriteLine($"Undefined local flags for '{((CPUJumpConditionEnum)parameter.Value)}'");
							break;
					}
					break;

				case CPUParameterTypeEnum.MemoryAddress:
				case CPUParameterTypeEnum.LEAMemoryAddress:
					locals |= CPUSegmentRegisterToLocal(parameter.DefaultDataSegment);

					switch (parameter.Value)
					{
						case 0:
						case 8:
						case 16:
							locals |= FlowGraphLocalEnum.BX;
							locals |= FlowGraphLocalEnum.SI;
							break;

						case 1:
						case 9:
						case 17:
							locals |= FlowGraphLocalEnum.BX;
							locals |= FlowGraphLocalEnum.DI;
							break;

						case 2:
						case 10:
						case 18:
							locals |= FlowGraphLocalEnum.BP;
							locals |= FlowGraphLocalEnum.SI;
							break;

						case 3:
						case 11:
						case 19:
							locals |= FlowGraphLocalEnum.BP;
							locals |= FlowGraphLocalEnum.DI;
							break;

						case 4:
						case 12:
						case 20:
							locals |= FlowGraphLocalEnum.SI;
							break;

						case 5:
						case 13:
						case 21:
							locals |= FlowGraphLocalEnum.DI;
							break;

						case 6:
							break;

						case 14:
						case 22:
							locals |= FlowGraphLocalEnum.BP;
							break;

						case 7:
						case 15:
						case 23:
							locals |= FlowGraphLocalEnum.BX;
							break;
					}
					break;
			}

			return locals;
		}

		private static FlowGraphLocalEnum DestinationParameterToRequiredLocals(CPUParameter parameter)
		{
			FlowGraphLocalEnum locals = FlowGraphLocalEnum.None;

			if (parameter.Size == CPUParameterSizeEnum.UInt32)
				throw new Exception("x32 addressing mode not yet implemented");

			switch (parameter.Type)
			{
				case CPUParameterTypeEnum.Immediate:
				case CPUParameterTypeEnum.SegmentOffset:
				case CPUParameterTypeEnum.Register:
				case CPUParameterTypeEnum.SegmentRegister:
				case CPUParameterTypeEnum.Condition:
					break;

				case CPUParameterTypeEnum.MemoryAddress:
				case CPUParameterTypeEnum.LEAMemoryAddress:
					locals |= CPUSegmentRegisterToLocal(parameter.DefaultDataSegment);

					switch (parameter.Value)
					{
						case 0:
						case 8:
						case 16:
							locals |= FlowGraphLocalEnum.BX;
							locals |= FlowGraphLocalEnum.SI;
							break;

						case 1:
						case 9:
						case 17:
							locals |= FlowGraphLocalEnum.BX;
							locals |= FlowGraphLocalEnum.DI;
							break;

						case 2:
						case 10:
						case 18:
							locals |= FlowGraphLocalEnum.BP;
							locals |= FlowGraphLocalEnum.SI;
							break;

						case 3:
						case 11:
						case 19:
							locals |= FlowGraphLocalEnum.BP;
							locals |= FlowGraphLocalEnum.DI;
							break;

						case 4:
						case 12:
						case 20:
							locals |= FlowGraphLocalEnum.SI;
							break;

						case 5:
						case 13:
						case 21:
							locals |= FlowGraphLocalEnum.DI;
							break;

						case 6:
							break;

						case 14:
						case 22:
							locals |= FlowGraphLocalEnum.BP;
							break;

						case 7:
						case 15:
						case 23:
							locals |= FlowGraphLocalEnum.BX;
							break;
					}
					break;
			}

			return locals;
		}

		private static FlowGraphLocalEnum DestinationParameterToDefinedLocals(CPUParameter parameter)
		{
			FlowGraphLocalEnum locals = FlowGraphLocalEnum.None;

			if (parameter.Size == CPUParameterSizeEnum.UInt32)
				throw new Exception("x32 addressing mode not yet implemented");

			switch (parameter.Type)
			{
				case CPUParameterTypeEnum.Immediate:
				case CPUParameterTypeEnum.SegmentOffset:
				case CPUParameterTypeEnum.Condition:
					break;

				case CPUParameterTypeEnum.Register:
					locals |= CPURegisterToLocal(parameter.RegisterValue);
					break;

				case CPUParameterTypeEnum.SegmentRegister:
					locals |= CPUSegmentRegisterToLocal(parameter.SegmentRegisterValue);
					break;

				case CPUParameterTypeEnum.MemoryAddress:
				case CPUParameterTypeEnum.LEAMemoryAddress:
					break;
			}

			return locals;
		}

		public static FlowGraphLocalEnum CPURegisterToLocal(CPURegisterEnum register)
		{
			switch (register)
			{
				case CPURegisterEnum.AL:
					return FlowGraphLocalEnum.AL;

				case CPURegisterEnum.AH:
					return FlowGraphLocalEnum.AH;

				case CPURegisterEnum.BL:
					return FlowGraphLocalEnum.BL;

				case CPURegisterEnum.BH:
					return FlowGraphLocalEnum.BH;

				case CPURegisterEnum.CL:
					return FlowGraphLocalEnum.CL;

				case CPURegisterEnum.CH:
					return FlowGraphLocalEnum.CH;

				case CPURegisterEnum.DL:
					return FlowGraphLocalEnum.DL;

				case CPURegisterEnum.DH:
					return FlowGraphLocalEnum.DH;

				case CPURegisterEnum.AX:
					return FlowGraphLocalEnum.AX;

				case CPURegisterEnum.BX:
					return FlowGraphLocalEnum.BX;

				case CPURegisterEnum.CX:
					return FlowGraphLocalEnum.CX;

				case CPURegisterEnum.DX:
					return FlowGraphLocalEnum.DX;

				case CPURegisterEnum.SI:
					return FlowGraphLocalEnum.SI;

				case CPURegisterEnum.DI:
					return FlowGraphLocalEnum.DI;

				case CPURegisterEnum.BP:
					return FlowGraphLocalEnum.BP;

				case CPURegisterEnum.SP:
					return FlowGraphLocalEnum.SP;

				case CPURegisterEnum.AX_DX:
					return FlowGraphLocalEnum.AX | FlowGraphLocalEnum.DX;

				default:
					return FlowGraphLocalEnum.None;
			}
		}

		public static FlowGraphLocalEnum CPUSegmentRegisterToLocal(CPUSegmentRegisterEnum segmentRegister)
		{
			switch (segmentRegister)
			{
				case CPUSegmentRegisterEnum.ES:
					return FlowGraphLocalEnum.ES;

				case CPUSegmentRegisterEnum.CS:
					return FlowGraphLocalEnum.CS;

				case CPUSegmentRegisterEnum.SS:
					return FlowGraphLocalEnum.SS;

				case CPUSegmentRegisterEnum.DS:
					return FlowGraphLocalEnum.DS;

				case CPUSegmentRegisterEnum.FS:
					return FlowGraphLocalEnum.FS;

				case CPUSegmentRegisterEnum.GS:
					return FlowGraphLocalEnum.GS;

				default:
					return FlowGraphLocalEnum.None;
			}
		}

		public static FlowGraphLocalEnum CPUFlagsToLocals(CPUFlagsEnum flags)
		{
			FlowGraphLocalEnum defined = FlowGraphLocalEnum.None;

			if (CPUInstruction.TestFlag(flags, CPUFlagsEnum.ZF))
			{
				defined |= FlowGraphLocalEnum.ZFlag;
			}

			if (CPUInstruction.TestFlag(flags, CPUFlagsEnum.CF))
			{
				defined |= FlowGraphLocalEnum.CFlag;
			}

			if (CPUInstruction.TestFlag(flags, CPUFlagsEnum.SF))
			{
				defined |= FlowGraphLocalEnum.SFlag;
			}

			if (CPUInstruction.TestFlag(flags, CPUFlagsEnum.OF))
			{
				defined |= FlowGraphLocalEnum.OFlag;
			}

			if (CPUInstruction.TestFlag(flags, CPUFlagsEnum.DF))
			{
				defined |= FlowGraphLocalEnum.DFlag;
			}

			return defined;
		}

		public void WriteGraphDOT(string fileName)
		{
			using (FileStream stream = new(fileName, FileMode.Create))
			{
				WriteGraphDOT(stream);
			}
		}

		public void WriteGraphDOT(Stream stream)
		{
			StreamWriter writer = new(stream, Encoding.ASCII);
			writer.NewLine = "\n";

			writer.WriteLine($"digraph {this.name}");
			writer.WriteLine("{");

			uint[] nodeAdresses = this.nodes.Keys.ToArray();

			if (this.startNode != null)
			{
				writer.WriteLine($"Start [shape=\"invhouse\", label=\"Start\\nRequires: {this.startNode.RequiredLocals}\\nDefines: {this.startNode.DefinedLocals}\"];");
			}
			else
			{
				writer.WriteLine($"Start [shape=\"invhouse\"];");
			}

			for (int i = 0; i < nodeAdresses.Length; i++)
			{
				FlowGraphNode node = this.nodes.GetValueByKey(nodeAdresses[i]);
				switch (node.NodeType)
				{
					case FlowGraphNodeTypeEnum.Start:
					case FlowGraphNodeTypeEnum.End:
						break;

					case FlowGraphNodeTypeEnum.Block:
						writer.WriteLine($"n{node.LinearAddress:x} [shape=\"box\", label=\"n{node.LinearAddress:x}\\nRequires: {node.RequiredLocals}\\nDefines: {node.DefinedLocals}\"];");
						break;

					case FlowGraphNodeTypeEnum.If:
						writer.WriteLine($"n{node.LinearAddress:x} [shape=\"diamond\", label=\"n{node.LinearAddress:x}\\nRequires: {node.RequiredLocals}\\nDefines: {node.DefinedLocals}\"];");
						break;

					case FlowGraphNodeTypeEnum.Switch:
						writer.WriteLine($"n{node.LinearAddress:x} [shape=\"hexagon\", label=\"n{node.LinearAddress:x}\\nRequires: {node.RequiredLocals}\\nDefines: {node.DefinedLocals}\"];");
						break;

					case FlowGraphNodeTypeEnum.DoWhile:
					case FlowGraphNodeTypeEnum.While:
					case FlowGraphNodeTypeEnum.For:
						writer.WriteLine($"n{node.LinearAddress:x} [shape=\"parallelogram\", label=\"n{node.LinearAddress:x}\\nRequires: {node.RequiredLocals}\\nDefines: {node.DefinedLocals}\"];");
						break;
				}

				for (int j = 0; j < node.ChildNodes.Count; j++)
				{
					FlowGraphNode childNode = node.ChildNodes[j];

					if (childNode.NodeType == FlowGraphNodeTypeEnum.End)
					{
						writer.Write($"n{node.LinearAddress:x}->End");
					}
					else if (node.NodeType == FlowGraphNodeTypeEnum.Start)
					{
						writer.Write($"Start->n{childNode.LinearAddress:x}");
					}
					else
					{
						writer.Write($"n{node.LinearAddress:x}->n{childNode.LinearAddress:x}");

						if (node.NodeType == FlowGraphNodeTypeEnum.Switch)
						{
							writer.Write($" [label=\"{node.SwitchValues[j]}\"]");
						}
					}

					writer.WriteLine(";");
				}
			}

			writer.WriteLine($"End [shape=\"house\"];");

			writer.WriteLine("}");

			writer.Flush();
			writer.Close();
		}

		public ProgramFunction ParentFunction { get => this.parentFunction; }

		public string Name { get => this.name; set => this.name = value; }

		public BDictionary<uint, FlowGraphNode> Nodes { get => this.nodes; }

		public FlowGraphNode? StartNode { get => this.startNode; set => this.startNode = value; }

		public BDictionary<uint, FlowGraphNode> EndNodes { get => this.endNodes; set => this.endNodes = value; }

		public FlowGraphLocalEnum RequiredLocals { get => this.requiredLocals; set => this.requiredLocals = value; }

		public FlowGraphLocalEnum DefinedLocals { get => this.definedLocals; set => this.definedLocals = value; }

		public bool BPFrame { get => this.hasBPFrame; }
	}
}
