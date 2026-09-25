using Disassembler;
using Disassembler.CPU;
using Disassembler.Formats.MZ;
using Disassembler.Formats.OMF;
using IRB.Collections.Generic;
using System.Reflection;
using System.Text;
using System.Text.RegularExpressions;

internal class Program
{
	private static void Main(string[] args)
	{
		string? path = Path.GetDirectoryName(Assembly.GetExecutingAssembly().Location);

		//OpCodeTable.ParseTable();

		path = string.IsNullOrEmpty(path) ? "" : path;

		if (args.Length > 0 && args[0].Equals("colonization", StringComparison.OrdinalIgnoreCase))
		{
			ParseColonization(path);
			return;
		}

		ParseDOSEXE(path, 3);
	}

	/// <summary>
	/// The values the MicroSoft C startup code hands to the program: the DGROUP data segment
	/// and the entry point of main().
	/// </summary>
	private record MSCStartup(ushort DataSegment, ushort MainSegment, ushort MainOffset,
		ushort Crt0Segment, ushort Crt0Offset);

	/// <summary>
	/// Locates main() by reading the MSC startup code, rather than by the fixed offsets the Civ1
	/// path uses. Civ1 and Colonization share the same __astart shape but reach it differently and
	/// sit at different offsets inside it, so one set of constants can't serve both.
	///
	/// On the Civ1 layout this finds exactly what the hardcoded offsets find.
	/// </summary>
	private static MSCStartup? FindMSCStartup(MZExecutable exe)
	{
		byte[] data = exe.Data;
		int entry = exe.InitialCS * 16 + exe.InitialIP;

		if (entry < 0 || entry + 0x51 >= data.Length)
			return null;

		ushort crt0Segment;
		ushort crt0Offset;

		if (data[entry] == 0x9a && data[entry + 5] == 0xea)
		{
			// overlay aware startup: CALL FAR <overlay manager init>, then JMP FAR <__astart>
			crt0Offset = exe.ReadUInt16(entry + 6);
			crt0Segment = exe.ReadUInt16(entry + 8);
		}
		else
		{
			// plain startup, it reaches __astart through a pointer in its own segment
			crt0Segment = exe.InitialCS;
			crt0Offset = exe.ReadUInt16(entry + 0x4f);
		}

		int crt0 = crt0Segment * 16 + crt0Offset;
		int length = Math.Min(0x140, data.Length - crt0);

		if (crt0 < 0 || length < 0x20)
			return null;

		// DGROUP, from the 'MOV DI, DGROUP' that precedes 'MOV SS, DI' in the stack setup
		ushort dataSegment = 0;

		for (int i = 0; i < Math.Min(0x30, length - 3); i++)
		{
			if (data[crt0 + i] == 0xbf)
			{
				dataSegment = exe.ReadUInt16(crt0 + i + 1);
				break;
			}
		}

		if (dataSegment == 0)
			return null;

		// main(), from the 'CALL FAR _main; PUSH AX; <call exit>' tail. The exit call is a far call
		// in Civ1 and a 'PUSH CS; CALL NEAR' in Colonization, so accept either.
		for (int i = length - 9; i >= 0; i--)
		{
			if (data[crt0 + i] != 0x9a || data[crt0 + i + 5] != 0x50)
				continue;

			byte after = data[crt0 + i + 6];

			if (after == 0x9a || after == 0xe8 || (after == 0x0e && data[crt0 + i + 7] == 0xe8))
			{
				return new MSCStartup(dataSegment, exe.ReadUInt16(crt0 + i + 3), exe.ReadUInt16(crt0 + i + 1),
					crt0Segment, crt0Offset);
			}
		}

		return null;
	}

	/// <summary>
	/// Colonization is built with the same MicroSoft C overlay scheme as Civ1. Its sound drivers
	/// are standalone MZ overlays with the same 512 byte header and 0000:0010 entry point as the
	/// Civ1 .CVL drivers, so they go through the same overlay path.
	/// </summary>
	private static void ParseColonization(string path)
	{
		string outputPath = path + $"{Path.DirectorySeparatorChar}Output";
		string codePath = outputPath + $"{Path.DirectorySeparatorChar}Colonization";

		if (!Directory.Exists(outputPath))
			Directory.CreateDirectory(outputPath);
		if (!Directory.Exists(codePath))
			Directory.CreateDirectory(codePath);

		ProcessColonizationMain(codePath);

		// Sound Drivers
		TranslateOverlayToIL(ProcessOverlay("COLONIZE", "ASound", "ASOUND.COL", "ASnd", 0, codePath), "ASound", codePath);
		TranslateOverlayToIL(ProcessOverlay("COLONIZE", "GSound", "GSOUND.COL", "GSnd", 0, codePath), "GSound", codePath);
		TranslateOverlayToIL(ProcessOverlay("COLONIZE", "PSound", "PSOUND.COL", "PSnd", 0, codePath), "PSound", codePath);
		TranslateOverlayToIL(ProcessOverlay("COLONIZE", "RSound", "RSOUND.COL", "RSnd", 0, codePath), "RSound", codePath);

		Console.WriteLine("Processing finished");
	}

	/// <summary>
	/// Disassembles VICEROY.EXE, the Colonization main executable.
	/// </summary>
	private static void ProcessColonizationMain(string codePath)
	{
		Console.WriteLine("* Loading VICEROY.EXE");

		try
		{
			MZExecutable mainEXE = new(string.Format("GameSRC{0}COLONIZE{0}VICEROY.EXE",
				Path.DirectorySeparatorChar));

			MSCStartup? startup = FindMSCStartup(mainEXE);

			if (startup == null)
			{
				Console.WriteLine("Warning: couldn't locate the MSC startup code in VICEROY.EXE");
				return;
			}

			Console.WriteLine("DGROUP {0:x4}, main at {1:x4}:{2:x4}",
				startup.DataSegment, startup.MainSegment, startup.MainOffset);

			if (mainEXE.OverlayContainer != null)
			{
				MZOverlayContainer container = mainEXE.OverlayContainer;

				Console.WriteLine("Overlay container at 0x{0:x}, {1} overlays, {2} bytes",
					container.Position, container.Overlays.Count,
					container.Overlays.Sum(o => o.Data.Length));

				WriteOverlayDirectory(container, codePath);
			}
			else if (mainEXE.UnknownTrailingDataPosition >= 0)
			{
				Console.WriteLine("Overlays not loaded, unrecognised container at file offset 0x{0:x}",
					mainEXE.UnknownTrailingDataPosition);
			}

			// no library matching yet, the runtime Colonization was linked against isn't identified
			List<ModuleMatch> matches = [];
			MainProgram mainProgram = new(mainEXE, matches, 0);
			mainProgram.DefaultDS = startup.DataSegment;

			Console.WriteLine("* Disassembling");

			// An overlaid function is represented in the main image by a 10 byte thunk:
			// CALL FAR <overlay manager>, then JMP FAR <segment 0000, overlay entry id>. The real
			// body only exists inside the overlay container, which isn't decoded yet, so there is
			// nothing to disassemble at the thunk itself.
			int mainAddress = startup.MainSegment * 16 + startup.MainOffset;
			bool mainIsOverlayThunk = mainAddress + 10 < mainEXE.Data.Length &&
				mainEXE.Data[mainAddress] == 0x9a && mainEXE.Data[mainAddress + 5] == 0xea;

			if (mainIsOverlayThunk)
			{
				Console.WriteLine("main() is an overlay thunk (JMP FAR {0:x4}:{1:x4}), its body lives in the overlay container",
					mainEXE.ReadUInt16(mainAddress + 8), mainEXE.ReadUInt16(mainAddress + 6));
				Console.WriteLine("Disassembling the startup code instead, that covers the code kept in the main image");

				// Start pulls in the overlays it calls, so a single unhandled function anywhere
				// below it used to take the whole run with it. Report and carry on instead, the
				// overlay entry points below cover the same ground from 600 separate roots.
				try
				{
					ProgramFunction start = mainProgram.Disassemble(0, startup.Crt0Segment, startup.Crt0Offset, "Start");
					start.ReturnValue = new ILVariable(start, ILVariableScopeEnum.LocalParameter, mainProgram.IntValueType, 0, 10);
				}
				catch (Exception ex)
				{
					Console.WriteLine("Warning: startup code disassembly failed: {0}", ex.Message);
				}

				DisassembleOverlayEntryPoints(mainEXE, mainProgram);
			}
			else
			{
				ProgramFunction function = mainProgram.Disassemble(0, startup.MainSegment, startup.MainOffset, "Main");
				function.ReturnValue = new ILVariable(function, ILVariableScopeEnum.LocalParameter, mainProgram.IntValueType, 0, 10);
			}

			Console.WriteLine("* Writing code");

			mainProgram.AssignOrdinals();

			uint[] segmentOffsets = mainProgram.Segments.Keys.ToArray();
			Array.Sort(segmentOffsets);

			for (int i = 0; i < segmentOffsets.Length; i++)
			{
				mainProgram.Segments.GetValueByKey(segmentOffsets[i]).WriteAsmCS(codePath, 0);
			}

			Console.WriteLine("Main segments: {0}", segmentOffsets.Length);
		}
		catch (Exception ex)
		{
			Console.WriteLine($"Warning: VICEROY.EXE disassembly failed: {ex.Message}");

			// a translator that says "not implemented" is expected, anything else is a bug
			if (ex.GetType() != typeof(Exception))
			{
				Console.WriteLine(ex.StackTrace);
			}
		}
	}

	/// <summary>
	/// Disassembles every overlay function the resident image has a thunk for.
	///
	/// The thunks are the only entry points into the overlays that can be found statically: an
	/// overlay's code is only reachable from the resident image through one, so the set of thunks
	/// is the set of externally callable functions. Anything an overlay calls internally is picked
	/// up by following its own calls from there.
	///
	/// A function that fails to disassemble is reported and skipped. There are 600 or so of them
	/// and they exercise far more of the instruction set than Civ1's sound drivers ever did, so
	/// one unimplemented corner shouldn't cost the whole run.
	/// </summary>
	private static void DisassembleOverlayEntryPoints(MZExecutable mainEXE, MainProgram mainProgram)
	{
		List<MZOverlayThunk> thunks = mainEXE.FindThunks();
		List<MZOverlayThunk> overlayThunks = thunks.FindAll(t => t.IsOverlay);

		Console.WriteLine("Thunks: {0} ({1} into overlays, {2} resident)",
			thunks.Count, overlayThunks.Count, thunks.Count - overlayThunks.Count);

		HashSet<int> seen = new();
		Dictionary<string, int> failures = new();
		int disassembled = 0;

		for (int i = 0; i < overlayThunks.Count; i++)
		{
			MZOverlayThunk thunk = overlayThunks[i];

			// several thunks can name the same function
			if (!seen.Add((thunk.OverlayNumber << 16) | thunk.TargetOffset))
			{
				continue;
			}

			try
			{
				mainProgram.Disassemble((ushort)thunk.OverlayNumber, 0, (ushort)thunk.TargetOffset, null);
				disassembled++;
			}
			catch (Exception ex)
			{
				failures.TryGetValue(ex.Message, out int count);
				failures[ex.Message] = count + 1;
			}
		}

		Console.WriteLine("Overlay functions: {0} of {1} entry points disassembled", disassembled, seen.Count);

		foreach (KeyValuePair<string, int> failure in failures.OrderByDescending(f => f.Value))
		{
			Console.WriteLine("  {0}x {1}", failure.Value, failure.Key);
		}
	}

	/// <summary>
	/// Writes the overlay directory next to the generated code, in the shape
	/// oracle/col1_overlay.py emits, so the two decoders can be compared entry by entry:
	///
	///   COL1_DIRECTORY_JSON=&lt;this file&gt; python3 -m unittest discover -s oracle
	/// </summary>
	private static void WriteOverlayDirectory(MZOverlayContainer container, string codePath)
	{
		string path = $"{codePath}{Path.DirectorySeparatorChar}overlay-directory.json";
		StringBuilder json = new();

		json.AppendLine("[");

		for (int i = 0; i < container.Overlays.Count; i++)
		{
			MZOverlay overlay = container.Overlays[i];

			json.AppendLine("  {");
			json.AppendLine($"    \"index\": {overlay.Index},");
			json.AppendLine($"    \"header\": {overlay.HeaderPosition},");
			json.AppendLine($"    \"code\": {overlay.CodePosition},");
			json.AppendLine($"    \"length\": {overlay.Data.Length},");
			json.AppendLine($"    \"relocations\": {overlay.Relocations.Count}");
			json.AppendLine(i < container.Overlays.Count - 1 ? "  }," : "  }");
		}

		json.AppendLine("]");

		File.WriteAllText(path, json.ToString());
		Console.WriteLine("Overlay directory written to {0}", path);
	}

	private static void ParseDOSEXE(string path, int version)
	{
		List<DataMatch> dataMatches = new();

		string outputPath = path + $"{Path.DirectorySeparatorChar}Output";
		string codePath = outputPath + $"{Path.DirectorySeparatorChar}Code{version}";

		if (!Directory.Exists(outputPath))
			Directory.CreateDirectory(outputPath);
		if (!Directory.Exists(codePath))
			Directory.CreateDirectory(codePath);

		// process Main code
		Console.WriteLine("* Loading EXE");

		MZExecutable mainEXE = new(string.Format("GameSRC{0}Civ1{0}CIV.EXE", 
			Path.DirectorySeparatorChar, version));
		
		//Console.WriteLine("OV index {0} flag {1}", mainEXE.OverlayIndex, mainEXE.OverlayFlag);

		int baseAddress = mainEXE.InitialCS * 16;
		ushort newDS = mainEXE.ReadUInt16(baseAddress + mainEXE.InitialIP + 0x2);
		ushort newSP = mainEXE.InitialSP;
		ushort mainCS;
		ushort mainIP;
		ushort tempIP = mainEXE.ReadUInt16(baseAddress + mainEXE.InitialIP + 0x4f);
		ushort dataCutoffOffset = 0;

		if (mainEXE.Data[baseAddress + tempIP + 0x1f] == 0x81 && mainEXE.Data[baseAddress + tempIP + 0x20] == 0xc4 &&
			mainEXE.Data[baseAddress + tempIP + 0x67] == 0x16 && mainEXE.Data[baseAddress + tempIP + 0x68] == 0x7 &&
			mainEXE.Data[baseAddress + tempIP + 0x69] == 0xfc && mainEXE.Data[baseAddress + tempIP + 0x6a] == 0xbf &&
			mainEXE.Data[baseAddress + tempIP + 0x97] == 0x9a)
		{
			// we have the correct offset
			newSP += mainEXE.ReadUInt16(baseAddress + tempIP + 0x21);
			newSP &= 0xfffe;

			mainCS = mainEXE.ReadUInt16(baseAddress + tempIP + 0x9a);
			mainIP = mainEXE.ReadUInt16(baseAddress + tempIP + 0x98);

			// This is the offset where data segment gets filled with zeroes
			dataCutoffOffset = mainEXE.ReadUInt16(baseAddress + tempIP + 0x6b);
		}
		else
		{
			throw new Exception("File is corrupted");
		}

		if (version == 1)
		{
			dataMatches.Add(new(0x2667, 7, 0x1a7));
			dataMatches.Add(new(0x280e, 0xb0, 0x38));
			dataMatches.Add(new(0x2846, 0x18, 0x60));
			dataMatches.Add(new(0x28a6, 0x50, 0x2812));
			dataMatches.Add(new(0x50b8, 0x5e, 0x11fe));
			dataMatches.Add(new(0x62b6, 0x60, 0x121));
			dataMatches.Add(new(0x63d7, 0x7c, 0x6d9)); // four bytes are unaccounted for

			/*MZExecutable mainEXE1 = new(string.Format("..{0}..{0}..{0}..{0}..{0}..{0}Games{0}Civilization{0}Civ I{0}Dos{0}CIV{1}.EXE",
				Path.DirectorySeparatorChar, 3));

			int baseAddress1 = mainEXE1.InitialCS * 16;
			ushort newDS1 = mainEXE1.ReadUInt16(baseAddress1 + mainEXE1.InitialIP + 0x2);
			ushort newSP1 = mainEXE1.InitialSP;
			ushort tempIP1 = mainEXE1.ReadUInt16(baseAddress1 + mainEXE1.InitialIP + 0x4f);
			ushort dataCutoffOffset1 = 0;

			if (mainEXE1.Data[baseAddress1 + tempIP1 + 0x1f] == 0x81 && mainEXE1.Data[baseAddress1 + tempIP1 + 0x20] == 0xc4 &&
				mainEXE1.Data[baseAddress1 + tempIP1 + 0x67] == 0x16 && mainEXE1.Data[baseAddress1 + tempIP1 + 0x68] == 0x7 &&
				mainEXE1.Data[baseAddress1 + tempIP1 + 0x69] == 0xfc && mainEXE1.Data[baseAddress1 + tempIP1 + 0x6a] == 0xbf &&
				mainEXE1.Data[baseAddress1 + tempIP1 + 0x97] == 0x9a)
			{
				// we have the correct offset
				newSP1 += mainEXE1.ReadUInt16(baseAddress1 + tempIP1 + 0x21);
				newSP1 &= 0xfffe;

				// This is the offset where data segment gets filled with zeroes
				dataCutoffOffset1 = mainEXE1.ReadUInt16(baseAddress1 + tempIP1 + 0x6b);
			}
			else
			{
				throw new Exception("File is corrupted");
			}

			// extract data blocks
			int dataLength1 = Math.Min(mainEXE.Data.Length - (int)MainProgram.ToLinearAddress(newDS, 0), 0xe000);
			int dataLength3 = Math.Min(mainEXE1.Data.Length - (int)MainProgram.ToLinearAddress(newDS1, 0), 0xe000);
			byte[] data1 = new byte[dataLength1];
			byte[] data3 = new byte[dataLength3];

			Array.Copy(mainEXE.Data, (int)MainProgram.ToLinearAddress(newDS, 0), data1, 0, dataLength1);
			Array.Copy(mainEXE1.Data, (int)MainProgram.ToLinearAddress(newDS1, 0), data3, 0, dataLength3);

			// calculate differences between data blocks
			// we want to normalize data positions from v1 to v3
			int dataPos1 = 0;
			int dataOffset1 = 0;
			int dataPos3 = 0;

			for (dataPos1 = 0, dataPos3 = 0; dataPos1 < dataLength1 && dataPos3 < dataLength3; dataPos1++, dataPos3++)
			{
				if (data1[dataPos1] != data3[dataPos3])
				{
					Console.Write($"Difference encountered at position 0x{dataPos1:x04}, ");

					// try to consilidate positions
					bool adjusted = false;

					for (int i = 0; i < 128; i++)
					{
						bool blockMatched = true;

						for (int j = 0; j < 4; j++)
						{
							if ((dataPos3 + i + j) >= dataLength3 || dataPos1 + j >= dataLength1 || data1[dataPos1 + j] != data3[dataPos3 + i + j])
							{
								blockMatched = false;
								break;
							}
						}

						if (blockMatched)
						{
							dataOffset1 += i;
							dataPos3 += i;
							adjusted = true;
							break;
						}
					}

					if (adjusted)
					{
						Console.WriteLine($"Offset readjusted to 0x{dataOffset1:x04} at position 0x{dataPos1:x04}");
					}
					else
					{
						Console.WriteLine($"Byte ignored at position 0x{dataPos1:x04}");
					}
				}
			}*/

			/*Console.WriteLine("Saving data segment");
			int dataLength1 = Math.Min(mainEXE.Data.Length - (int)MainProgram.ToLinearAddress(newDS, 0), 0xe000);
			byte[] data1Old = new byte[dataLength1];
			byte[] data1New = new byte[1];

			Array.Copy(mainEXE.Data, (int)MainProgram.ToLinearAddress(newDS, 0), data1Old, 0, dataLength1);

			if (dataMatches.Count > 0 && dataMatches[0].Offset > 0)
			{
				DataMatch match = dataMatches[0];

				Array.Resize<byte>(ref data1New, match.Position);

				for (int j = 0; j < match.Position; j++)
				{
					data1New[j] = data1Old[j];
				}
			}

			for (int i = 0; i < dataMatches.Count; i++)
			{
				DataMatch match = dataMatches[i];
				int oldPos = match.Position;
				int newPos = match.Position + match.Offset;

				if (data1New.Length < newPos + match.Length)
				{
					Array.Resize<byte>(ref data1New, newPos + match.Length);

					for (int j = 0; j < match.Length; j++)
					{
						data1New[newPos + j] = 0xff;
					}
				}

				for (int j = 0; j < match.Length; j++)
				{
					data1New[newPos + j] = data1Old[oldPos + j];
				}
			}

			FileStream dataFile1 = new($"{outputPath}/Data{version}Old.bin", FileMode.Create, FileAccess.ReadWrite);
			dataFile1.Write(data1Old, 0, data1Old.Length);
			dataFile1.Close();

			dataFile1 = new($"{outputPath}/Data{version}New.bin", FileMode.Create, FileAccess.ReadWrite);
			dataFile1.Write(data1New, 0, data1New.Length);
			dataFile1.Close();*/
		}

		Console.WriteLine("Saving data segment");
		FileStream dataFile = new($"{outputPath}/Data{version}.bin", FileMode.Create, FileAccess.ReadWrite);
		dataFile.Write(mainEXE.Data, (int)MainProgram.ToLinearAddress(newDS, 0),
			Math.Min(mainEXE.Data.Length - (int)MainProgram.ToLinearAddress(newDS, 0), newSP - 1));
		dataFile.Close();

		Console.WriteLine("Loading library MLIBC7");
        OMFLibrary library1 = new(string.Format("DOS Compilers{0}MSC{0}v5{0}Installed{0}MSC{0}LIB{0}MLIBC7.LIB",
            Path.DirectorySeparatorChar));

        // unique set of segment names
        BHashSet<string> segmentNames = [];
		BHashSet<string> groupNames = [];

		for (int i = 0; i < library1.Modules.Count; i++)
		{
			OMFOBJModule module = library1.Modules[i];

			for (int j = 0; j < module.DataRecords.Count; j++)
			{
				OMFDataRecord data = module.DataRecords[j];
				if (data.Segment != null)
				{
					segmentNames.Add(data.Segment.Name);
				}
			}

			for (int j = 0; j < module.SegmentGroups.Count; j++)
			{
				OMFSegmentGroupDefinition group = module.SegmentGroups[j];
				groupNames.Add(group.Name);
			}
		}

		Console.WriteLine("Used segment names:");
		for (int i = 0; i < segmentNames.Count; i++)
		{
			Console.WriteLine(segmentNames[i]);
		}

		Console.WriteLine("Group names:");
		for (int i = 0; i < groupNames.Count; i++)
		{
			Console.WriteLine(groupNames[i]);
		}

		List<ModuleMatch> libraryMatches = [];
		Console.WriteLine("* Matching MLIBC7");
		OMFLibrary.MatchLibrary(library1, mainEXE, libraryMatches);

		MainProgram mainProgram = new(mainEXE, libraryMatches, version);
		mainProgram.DefaultDS = newDS;
		
		Console.WriteLine("Library modules count {1}, matches {0}", libraryMatches.Count, library1.Modules.Count);
		
		#region MS C 5.1 API functions

		// Word registers
		ILValueType wordRegs = new("WORDREGS", ILBaseValueTypeEnum.Struct);
		wordRegs.MemberObjects.Add(new("ax", ILBaseValueTypeEnum.UInt16));
		wordRegs.MemberObjects.Add(new("bx", ILBaseValueTypeEnum.UInt16));
		wordRegs.MemberObjects.Add(new("cx", ILBaseValueTypeEnum.UInt16));
		wordRegs.MemberObjects.Add(new("dx", ILBaseValueTypeEnum.UInt16));
		wordRegs.MemberObjects.Add(new("si", ILBaseValueTypeEnum.UInt16));
		wordRegs.MemberObjects.Add(new("di", ILBaseValueTypeEnum.UInt16));
		wordRegs.MemberObjects.Add(new("cflag", ILBaseValueTypeEnum.UInt16));
		mainProgram.CustomValueTypes.Add(wordRegs.TypeName, wordRegs);

		// Byte registers
		ILValueType byteRegs = new("BYTEREGS", ILBaseValueTypeEnum.Struct);
		byteRegs.MemberObjects.Add(new("al", ILBaseValueTypeEnum.UInt8));
		byteRegs.MemberObjects.Add(new("ah", ILBaseValueTypeEnum.UInt8));
		byteRegs.MemberObjects.Add(new("bl", ILBaseValueTypeEnum.UInt8));
		byteRegs.MemberObjects.Add(new("bh", ILBaseValueTypeEnum.UInt8));
		byteRegs.MemberObjects.Add(new("cl", ILBaseValueTypeEnum.UInt8));
		byteRegs.MemberObjects.Add(new("ch", ILBaseValueTypeEnum.UInt8));
		byteRegs.MemberObjects.Add(new("dl", ILBaseValueTypeEnum.UInt8));
		byteRegs.MemberObjects.Add(new("dh", ILBaseValueTypeEnum.UInt8));
		mainProgram.CustomValueTypes.Add(byteRegs.TypeName, byteRegs);

		// Registers union (Overlays the corresponding word and byte registers)
		ILValueType regs = new("REGS", ILBaseValueTypeEnum.Union);
		regs.MemberObjects.Add(new("x", ILBaseValueTypeEnum.Struct, wordRegs));
		regs.MemberObjects.Add(new("h", ILBaseValueTypeEnum.Struct, byteRegs));
		mainProgram.CustomValueTypes.Add(regs.TypeName, regs);

		// Define FILE structure as a single pointer
		ILValueType file = new("FILE", ILBaseValueTypeEnum.DirectObject);
		mainProgram.CustomValueTypes.Add(file.TypeName, file);

		ILValueType filePtr = new("FILE *", ILBaseValueTypeEnum.Ptr16, file);
		mainProgram.CustomValueTypes.Add(filePtr.TypeName, filePtr);

		ILValueType diskinfo_t = new("diskinfo_t", ILBaseValueTypeEnum.Struct);
		diskinfo_t.MemberObjects.Add(new("drive", ILBaseValueTypeEnum.UInt16));
		diskinfo_t.MemberObjects.Add(new("head", ILBaseValueTypeEnum.UInt16));
		diskinfo_t.MemberObjects.Add(new("track", ILBaseValueTypeEnum.UInt16));
		diskinfo_t.MemberObjects.Add(new("sector", ILBaseValueTypeEnum.UInt16));
		diskinfo_t.MemberObjects.Add(new("nsectors", ILBaseValueTypeEnum.UInt16));
		diskinfo_t.MemberObjects.Add(new("buffer", ILBaseValueTypeEnum.Ptr32));
		mainProgram.CustomValueTypes.Add(diskinfo_t.TypeName, diskinfo_t);

		// int int86(int intnum, union _REGS * inregs, union _REGS * outregs );
		mainProgram.APIFunctions.Add("int86", new APIFunctionDefinition("int86", ProgramFunctionOptionsEnum.Cdecl,
			[new("intnum", ILVariableScopeEnum.LocalParameter, mainProgram.IntValueType, 6, 10),
				new("inregs", ILVariableScopeEnum.LocalParameter, new(ILBaseValueTypeEnum.Ptr16, regs), 8, 10),
				new("outregs", ILVariableScopeEnum.LocalParameter, new(ILBaseValueTypeEnum.Ptr16, regs), 10, 10)], 
			new(ILVariableScopeEnum.ReturnValue, mainProgram.IntValueType, 0, 10)));

		// int intdos(union REGS * inregs, union REGS * outregs);
		mainProgram.APIFunctions.Add("intdos", new APIFunctionDefinition("intdos", ProgramFunctionOptionsEnum.Cdecl,
			[new("inregs", ILVariableScopeEnum.LocalParameter, new(ILBaseValueTypeEnum.Ptr16, regs), 6, 10),
				new("outregs", ILVariableScopeEnum.LocalParameter, new(ILBaseValueTypeEnum.Ptr16, regs), 8, 10)],
			new(ILVariableScopeEnum.ReturnValue, mainProgram.IntValueType, 0, 10)));

		// void (_cdecl _interrupt far * _dos_getvect(unsigned intnum))();
		mainProgram.APIFunctions.Add("_dos_getvect", new APIFunctionDefinition("_dos_getvect", ProgramFunctionOptionsEnum.Cdecl,
			[new("intnum", ILVariableScopeEnum.LocalParameter, mainProgram.UnsignedIntValueType, 6, 10)],
			new(ILVariableScopeEnum.ReturnValue, new(ILBaseValueTypeEnum.Ptr32), 0, 10)));

		// void _dos_setvect(unsigned intnum, void (_cdecl _interrupt far * handler)());
		mainProgram.APIFunctions.Add("_dos_setvect", new APIFunctionDefinition("_dos_setvect", ProgramFunctionOptionsEnum.Cdecl,
			[new("intnum", ILVariableScopeEnum.LocalParameter, mainProgram.UnsignedIntValueType, 6, 10),
				new("handler", ILVariableScopeEnum.LocalParameter, new(ILBaseValueTypeEnum.Ptr32), 8, 10)],
			new(ILVariableScopeEnum.ReturnValue, mainProgram.VoidValueType, 0, 10)));

		// unsigned _dos_open(const char * path, unsigned mode, int * handle);
		mainProgram.APIFunctions.Add("_dos_open", new APIFunctionDefinition("_dos_open", ProgramFunctionOptionsEnum.Cdecl,
			[new("path", ILVariableScopeEnum.LocalParameter, mainProgram.StringValueType, 6, 10),
				new("mode", ILVariableScopeEnum.LocalParameter, mainProgram.UnsignedIntValueType, 8, 10),
				new("handle", ILVariableScopeEnum.LocalParameter, new(ILBaseValueTypeEnum.Ptr16, mainProgram.IntValueType), 10, 10)],
			new(ILVariableScopeEnum.ReturnValue, mainProgram.UnsignedIntValueType, 0, 10)));

		// unsigned _dos_close(int handle);
		mainProgram.APIFunctions.Add("_dos_close", new APIFunctionDefinition("_dos_close", ProgramFunctionOptionsEnum.Cdecl,
			[new("handle", ILVariableScopeEnum.LocalParameter, mainProgram.IntValueType, 6, 10)],
			new(ILVariableScopeEnum.ReturnValue, mainProgram.UnsignedIntValueType, 0, 10)));

		// unsigned _dos_read(int handle, void far *buffer, unsigned count, unsigned * numread);
		mainProgram.APIFunctions.Add("_dos_read", new APIFunctionDefinition("_dos_read", ProgramFunctionOptionsEnum.Cdecl,
			[new("handle", ILVariableScopeEnum.LocalParameter, mainProgram.IntValueType, 6, 10),
				new("buffer", ILVariableScopeEnum.LocalParameter, new(ILBaseValueTypeEnum.Ptr32, mainProgram.VoidValueType), 8, 10),
				new("count", ILVariableScopeEnum.LocalParameter, mainProgram.UnsignedIntValueType, 12, 10),
				new("numread", ILVariableScopeEnum.LocalParameter, new(ILBaseValueTypeEnum.Ptr16, mainProgram.UnsignedIntValueType), 14, 10)],
			new(ILVariableScopeEnum.ReturnValue, mainProgram.UnsignedIntValueType, 0, 10)));

		// unsigned _dos_freemem(unsigned seg); 
		mainProgram.APIFunctions.Add("_dos_freemem", new APIFunctionDefinition("_dos_freemem", ProgramFunctionOptionsEnum.Cdecl,
			[new("seg", ILVariableScopeEnum.LocalParameter, mainProgram.UnsignedIntValueType, 6, 10)],
			new(ILVariableScopeEnum.ReturnValue, mainProgram.UnsignedIntValueType, 0, 10)));

		// void _dos_getdrive(unsigned * drive);
		mainProgram.APIFunctions.Add("_dos_getdrive", new APIFunctionDefinition("_dos_getdrive", ProgramFunctionOptionsEnum.Cdecl,
			[new("drive", ILVariableScopeEnum.LocalParameter, new(ILBaseValueTypeEnum.Ptr16, mainProgram.UnsignedIntValueType), 6, 10)],
			new(ILVariableScopeEnum.ReturnValue, mainProgram.VoidValueType, 0, 10)));

		// unsigned _bios_disk(unsigned service, struct _diskinfo_t * diskinfo);
		mainProgram.APIFunctions.Add("_bios_disk", new APIFunctionDefinition("_bios_disk", ProgramFunctionOptionsEnum.Cdecl,
			[new("service", ILVariableScopeEnum.LocalParameter, mainProgram.UnsignedIntValueType, 6, 10),
				new("diskinfo", ILVariableScopeEnum.LocalParameter, new(ILBaseValueTypeEnum.Ptr16, diskinfo_t), 8, 10),],
			new(ILVariableScopeEnum.ReturnValue, mainProgram.UnsignedIntValueType, 0, 10)));

		// FILE * fopen(const char * filename, const char * mode);
		mainProgram.APIFunctions.Add("fopen", new APIFunctionDefinition("fopen", ProgramFunctionOptionsEnum.Cdecl,
			[new("filename", ILVariableScopeEnum.LocalParameter, mainProgram.StringValueType, 6, 10),
				new("mode", ILVariableScopeEnum.LocalParameter, mainProgram.StringValueType, 8, 10)],
			new(ILVariableScopeEnum.ReturnValue, filePtr, 0, 10)));

		// int fscanf(FILE * stream, const char * format [, argument ]...);
		mainProgram.APIFunctions.Add("fscanf", new APIFunctionDefinition("fscanf", ProgramFunctionOptionsEnum.Cdecl | ProgramFunctionOptionsEnum.VariableArguments,
			[new("stream", ILVariableScopeEnum.LocalParameter, filePtr, 6, 10),
				new("format", ILVariableScopeEnum.LocalParameter, mainProgram.StringValueType, 8, 10),
				new("args", ILVariableScopeEnum.LocalParameter, new(ILBaseValueTypeEnum.Ptr16, mainProgram.VoidValueType), 10, 10, true)],
			new(ILVariableScopeEnum.ReturnValue, mainProgram.IntValueType, 0, 10)));

		// unsigned int fread(void * buffer, unsigned int size, unsigned int count, FILE * stream);
		mainProgram.APIFunctions.Add("fread", new APIFunctionDefinition("fread", ProgramFunctionOptionsEnum.Cdecl,
			[new("buffer", ILVariableScopeEnum.LocalParameter, new(ILBaseValueTypeEnum.Ptr16, mainProgram.VoidValueType), 6, 10),
				new("size", ILVariableScopeEnum.LocalParameter, mainProgram.UnsignedIntValueType, 8, 10),
				new("count", ILVariableScopeEnum.LocalParameter, mainProgram.UnsignedIntValueType, 10, 10),
				new("stream", ILVariableScopeEnum.LocalParameter, filePtr, 12, 10)],
			new(ILVariableScopeEnum.ReturnValue, mainProgram.UnsignedIntValueType, 0, 10)));

		// unsigned int fwrite(const void * buffer, unsigned int size, unsigned int count, FILE * stream);
		mainProgram.APIFunctions.Add("fwrite", new APIFunctionDefinition("fwrite", ProgramFunctionOptionsEnum.Cdecl,
			[new("buffer", ILVariableScopeEnum.LocalParameter, new(ILBaseValueTypeEnum.Ptr16, mainProgram.VoidValueType), 6, 10),
				new("size", ILVariableScopeEnum.LocalParameter, mainProgram.UnsignedIntValueType, 8, 10),
				new("count", ILVariableScopeEnum.LocalParameter, mainProgram.UnsignedIntValueType, 10, 10),
				new("stream", ILVariableScopeEnum.LocalParameter, filePtr, 12, 10)],
			new(ILVariableScopeEnum.ReturnValue, mainProgram.UnsignedIntValueType, 0, 10)));

		// long ftell(FILE * stream);
		mainProgram.APIFunctions.Add("ftell", new APIFunctionDefinition("ftell", ProgramFunctionOptionsEnum.Cdecl,
			[new("stream", ILVariableScopeEnum.LocalParameter, filePtr, 6, 10)],
			new(ILVariableScopeEnum.ReturnValue, mainProgram.LongValueType, 0, 10)));

		// int fseek(FILE * stream, long offset, int origin);
		mainProgram.APIFunctions.Add("fseek", new APIFunctionDefinition("fseek", ProgramFunctionOptionsEnum.Cdecl,
			[new("stream", ILVariableScopeEnum.LocalParameter, filePtr, 6, 10),
				new("offset", ILVariableScopeEnum.LocalParameter, mainProgram.LongValueType, 8, 10),
				new("origin", ILVariableScopeEnum.LocalParameter, mainProgram.IntValueType, 12, 10)],
			new(ILVariableScopeEnum.ReturnValue, mainProgram.IntValueType, 0, 10)));

		// int fclose(FILE *stream);
		mainProgram.APIFunctions.Add("fclose", new APIFunctionDefinition("fclose", ProgramFunctionOptionsEnum.Cdecl,
			[new("stream", ILVariableScopeEnum.LocalParameter, filePtr, 6, 10)],
			new(ILVariableScopeEnum.ReturnValue, mainProgram.IntValueType, 0, 10)));

		// int open(const char * filename, int oflag [, int pmode]); 
		mainProgram.APIFunctions.Add("open", new APIFunctionDefinition("open", ProgramFunctionOptionsEnum.Cdecl,
			[new("filename", ILVariableScopeEnum.LocalParameter, mainProgram.StringValueType, 6, 10),
				new("oflag", ILVariableScopeEnum.LocalParameter, mainProgram.IntValueType, 8, 10),
				new("pmode", ILVariableScopeEnum.LocalParameter, mainProgram.IntValueType, 10, 10)],
			new(ILVariableScopeEnum.ReturnValue, mainProgram.IntValueType, 0, 10)));

		// int read(int handle, void * buffer, unsigned int count);
		mainProgram.APIFunctions.Add("read", new APIFunctionDefinition("read", ProgramFunctionOptionsEnum.Cdecl,
			[new("handle", ILVariableScopeEnum.LocalParameter, mainProgram.IntValueType, 6, 10),
				new("buffer", ILVariableScopeEnum.LocalParameter, new(ILBaseValueTypeEnum.Ptr16, mainProgram.VoidValueType), 8, 10),
				new("count", ILVariableScopeEnum.LocalParameter, mainProgram.UnsignedIntValueType, 10, 10)],
			new(ILVariableScopeEnum.ReturnValue, mainProgram.IntValueType, 0, 10)));

		// int write(int handle, void * buffer, unsigned int count);
		mainProgram.APIFunctions.Add("write", new APIFunctionDefinition("write", ProgramFunctionOptionsEnum.Cdecl,
			[new("handle", ILVariableScopeEnum.LocalParameter, mainProgram.IntValueType, 6, 10),
				new("buffer", ILVariableScopeEnum.LocalParameter, new(ILBaseValueTypeEnum.Ptr16, mainProgram.VoidValueType), 8, 10),
				new("count", ILVariableScopeEnum.LocalParameter, mainProgram.UnsignedIntValueType, 10, 10)],
			new(ILVariableScopeEnum.ReturnValue, mainProgram.IntValueType, 0, 10)));

		// long lseek(int handle, long offset, int origin);
		mainProgram.APIFunctions.Add("lseek", new APIFunctionDefinition("lseek", ProgramFunctionOptionsEnum.Cdecl,
			[new("handle", ILVariableScopeEnum.LocalParameter, mainProgram.IntValueType, 6, 10),
				new("offset", ILVariableScopeEnum.LocalParameter, mainProgram.LongValueType, 8, 10),
				new("origin", ILVariableScopeEnum.LocalParameter, mainProgram.IntValueType, 12, 10)],
			new(ILVariableScopeEnum.ReturnValue, mainProgram.LongValueType, 0, 10)));

		// int close(int handle);
		mainProgram.APIFunctions.Add("close", new APIFunctionDefinition("close", ProgramFunctionOptionsEnum.Cdecl,
			[new("handle", ILVariableScopeEnum.LocalParameter, mainProgram.IntValueType, 6, 10)],
			new(ILVariableScopeEnum.ReturnValue, mainProgram.IntValueType, 0, 10)));

		// char * strcpy(char * string1, const char * string2);
		mainProgram.APIFunctions.Add("strcpy", new APIFunctionDefinition("strcpy", ProgramFunctionOptionsEnum.Cdecl,
			[new("string1", ILVariableScopeEnum.LocalParameter, mainProgram.StringValueType, 6, 10),
				new("string2", ILVariableScopeEnum.LocalParameter, mainProgram.StringValueType, 8, 10)],
			new(ILVariableScopeEnum.ReturnValue, mainProgram.StringValueType, 0, 10)));

		// char * strcat(char * string1, const char * string2);
		mainProgram.APIFunctions.Add("strcat", new APIFunctionDefinition("strcat", ProgramFunctionOptionsEnum.Cdecl,
			[new("string1", ILVariableScopeEnum.LocalParameter, mainProgram.StringValueType, 6, 10),
				new("string2", ILVariableScopeEnum.LocalParameter, mainProgram.StringValueType, 8, 10)],
			new(ILVariableScopeEnum.ReturnValue, mainProgram.StringValueType, 0, 10)));

		// unsigned int strlen(const char * string);
		mainProgram.APIFunctions.Add("strlen", new APIFunctionDefinition("strlen", ProgramFunctionOptionsEnum.Cdecl,
			[new("string", ILVariableScopeEnum.LocalParameter, mainProgram.StringValueType, 6, 10)],
			new(ILVariableScopeEnum.ReturnValue, mainProgram.UnsignedIntValueType, 0, 10)));

		// int strnicmp(const char * string1, const char * string2, unsigned int count);
		mainProgram.APIFunctions.Add("strnicmp", new APIFunctionDefinition("strnicmp", ProgramFunctionOptionsEnum.Cdecl,
			[new("string1", ILVariableScopeEnum.LocalParameter, mainProgram.StringValueType, 6, 10),
				new("string2", ILVariableScopeEnum.LocalParameter, mainProgram.StringValueType, 8, 10),
				new("count", ILVariableScopeEnum.LocalParameter, mainProgram.UnsignedIntValueType, 10, 10)],
			new(ILVariableScopeEnum.ReturnValue, mainProgram.IntValueType, 0, 10)));

		// int stricmp(const char * string1, const char * string2);
		mainProgram.APIFunctions.Add("stricmp", new APIFunctionDefinition("stricmp", ProgramFunctionOptionsEnum.Cdecl,
			[new("string1", ILVariableScopeEnum.LocalParameter, mainProgram.StringValueType, 6, 10),
				new("string2", ILVariableScopeEnum.LocalParameter, mainProgram.StringValueType, 8, 10)],
			new(ILVariableScopeEnum.ReturnValue, mainProgram.IntValueType, 0, 10)));

		// char * strstr(const char * string1, const char * string2);
		mainProgram.APIFunctions.Add("strstr", new APIFunctionDefinition("strstr", ProgramFunctionOptionsEnum.Cdecl,
			[new("string1", ILVariableScopeEnum.LocalParameter, mainProgram.StringValueType, 6, 10),
				new("string2", ILVariableScopeEnum.LocalParameter, mainProgram.StringValueType, 8, 10)],
			new(ILVariableScopeEnum.ReturnValue, mainProgram.StringValueType, 0, 10)));

		// char * strupr(char * string);
		mainProgram.APIFunctions.Add("strupr", new APIFunctionDefinition("strupr", ProgramFunctionOptionsEnum.Cdecl,
			[new("string", ILVariableScopeEnum.LocalParameter, mainProgram.StringValueType, 6, 10)],
			new(ILVariableScopeEnum.ReturnValue, mainProgram.StringValueType, 0, 10)));

		// char * itoa(int value, char * string, int radix);
		mainProgram.APIFunctions.Add("itoa", new APIFunctionDefinition("itoa", ProgramFunctionOptionsEnum.Cdecl,
			[new("value", ILVariableScopeEnum.LocalParameter, mainProgram.IntValueType, 6, 10),
				new("string", ILVariableScopeEnum.LocalParameter, mainProgram.StringValueType, 8, 10),
				new("radix", ILVariableScopeEnum.LocalParameter, mainProgram.IntValueType, 10, 10)],
			new(ILVariableScopeEnum.ReturnValue, mainProgram.StringValueType, 0, 10)));

		// void * memcpy(void * dest, const void * src, unsigned int count);
		mainProgram.APIFunctions.Add("memcpy", new APIFunctionDefinition("memcpy", ProgramFunctionOptionsEnum.Cdecl,
			[new("dest", ILVariableScopeEnum.LocalParameter, new(ILBaseValueTypeEnum.Ptr16, mainProgram.VoidValueType), 6, 10),
				new("src", ILVariableScopeEnum.LocalParameter, new(ILBaseValueTypeEnum.Ptr16, mainProgram.VoidValueType), 8, 10),
				new("count", ILVariableScopeEnum.LocalParameter, mainProgram.UnsignedIntValueType, 10, 10)],
			new(ILVariableScopeEnum.ReturnValue, new(ILBaseValueTypeEnum.Ptr16, mainProgram.VoidValueType), 0, 10)));

		// void movedata(unsigned int srcseg, unsigned int srcoff, unsigned int destseg, unsigned int destoff, unsigned int count);
		mainProgram.APIFunctions.Add("movedata", new APIFunctionDefinition("movedata", ProgramFunctionOptionsEnum.Cdecl,
			[new("srcseg", ILVariableScopeEnum.LocalParameter, mainProgram.UnsignedIntValueType, 6, 10),
				new("srcoff", ILVariableScopeEnum.LocalParameter, mainProgram.UnsignedIntValueType, 8, 10),
				new("destseg", ILVariableScopeEnum.LocalParameter, mainProgram.UnsignedIntValueType, 10, 10),
				new("destoff", ILVariableScopeEnum.LocalParameter, mainProgram.UnsignedIntValueType, 12, 10),
				new("count", ILVariableScopeEnum.LocalParameter, mainProgram.UnsignedIntValueType, 14, 10)],
			new(ILVariableScopeEnum.ReturnValue, mainProgram.VoidValueType, 0, 10)));

		// void * memset(void * dest, int c, unsigned int count);
		mainProgram.APIFunctions.Add("memset", new APIFunctionDefinition("memset", ProgramFunctionOptionsEnum.Cdecl,
			[new("dest", ILVariableScopeEnum.LocalParameter, new(ILBaseValueTypeEnum.Ptr16, mainProgram.VoidValueType), 6, 10),
				new("c", ILVariableScopeEnum.LocalParameter, mainProgram.IntValueType, 8, 10),
				new("count", ILVariableScopeEnum.LocalParameter, mainProgram.UnsignedIntValueType, 10, 10)],
			new(ILVariableScopeEnum.ReturnValue, new(ILBaseValueTypeEnum.Ptr16, mainProgram.VoidValueType), 0, 10)));

		// int kbhit();
		mainProgram.APIFunctions.Add("kbhit", new APIFunctionDefinition("kbhit", ProgramFunctionOptionsEnum.Cdecl,
			[], new(ILVariableScopeEnum.ReturnValue, mainProgram.IntValueType, 0, 10)));

		// int getch();
		mainProgram.APIFunctions.Add("getch", new APIFunctionDefinition("getch", ProgramFunctionOptionsEnum.Cdecl,
			[], new(ILVariableScopeEnum.ReturnValue, mainProgram.IntValueType, 0, 10)));

		// int abs(int n);
		mainProgram.APIFunctions.Add("abs", new APIFunctionDefinition("abs", ProgramFunctionOptionsEnum.Cdecl,
			[new("n", ILVariableScopeEnum.LocalParameter, mainProgram.IntValueType, 6, 10)],
			new(ILVariableScopeEnum.ReturnValue, mainProgram.IntValueType, 0, 10)));

		// void srand(unsigned int seed);
		mainProgram.APIFunctions.Add("srand", new APIFunctionDefinition("srand", ProgramFunctionOptionsEnum.Cdecl,
			[new("seed", ILVariableScopeEnum.LocalParameter, mainProgram.UnsignedIntValueType, 6, 10)],
			new(ILVariableScopeEnum.ReturnValue, mainProgram.VoidValueType, 0, 10)));

		// int rand();
		mainProgram.APIFunctions.Add("rand", new APIFunctionDefinition("rand", ProgramFunctionOptionsEnum.Cdecl,
			[], new(ILVariableScopeEnum.ReturnValue, mainProgram.IntValueType, 0, 10)));

		// long time(long * timer);
		mainProgram.APIFunctions.Add("time", new APIFunctionDefinition("time", ProgramFunctionOptionsEnum.Cdecl,
			[new("timer", ILVariableScopeEnum.LocalParameter, mainProgram.LongValueType, 6, 10)],
			new(ILVariableScopeEnum.ReturnValue, mainProgram.LongValueType, 0, 10)));

		// void perror(const char * string);
		mainProgram.APIFunctions.Add("perror", new APIFunctionDefinition("perror", ProgramFunctionOptionsEnum.Cdecl,
			[new("string", ILVariableScopeEnum.LocalParameter, mainProgram.StringValueType, 6, 10)],
			new(ILVariableScopeEnum.ReturnValue, mainProgram.VoidValueType, 0, 10)));

		// void exit(int status);
		mainProgram.APIFunctions.Add("exit", new APIFunctionDefinition("exit", ProgramFunctionOptionsEnum.Cdecl,
			[new("status", ILVariableScopeEnum.LocalParameter, mainProgram.IntValueType, 6, 10)],
			new(ILVariableScopeEnum.ReturnValue, mainProgram.VoidValueType, 0, 10)));

		// _aFlshl
		mainProgram.APIFunctions.Add("_aFlshl", new APIFunctionDefinition("_aFlshl", ProgramFunctionOptionsEnum.CompilerInternal, [], 
			new(ILVariableScopeEnum.ReturnValue, mainProgram.VoidValueType, 0, 0)));

		// _aFlshr
		mainProgram.APIFunctions.Add("_aFlshr", new APIFunctionDefinition("_aFlshr", ProgramFunctionOptionsEnum.CompilerInternal, [],
			new(ILVariableScopeEnum.ReturnValue, mainProgram.VoidValueType, 0, 0)));

		// _aFlmul
		mainProgram.APIFunctions.Add("_aFlmul", new APIFunctionDefinition("_aFlmul", ProgramFunctionOptionsEnum.Pascal, 
			[new("multiplicand", ILVariableScopeEnum.LocalParameter, mainProgram.LongValueType, 6, 10),
				new("multiplier", ILVariableScopeEnum.LocalParameter, mainProgram.LongValueType, 10, 10)],
			new(ILVariableScopeEnum.ReturnValue, mainProgram.LongValueType, 0, 10)));

		#endregion

		Console.WriteLine("* Disassembling");

		// we start by decompiling Main function
		ProgramFunction function = mainProgram.Disassemble(0, mainCS, mainIP, "Main");
		function.ReturnValue = new ILVariable(function, ILVariableScopeEnum.LocalParameter, mainProgram.IntValueType, 0, 10);

		// a way to dissassemble functions which are referenced by a pointer, like interrupt functions and such
		switch (version)
		{
			case 1:
				// Timer handler referenced by Seg0, Fn1
				mainProgram.Disassemble(0, 0x0000, 0x199, "TimerHandler");

				// Error handler referenced by Seg0, Fn23
				mainProgram.Disassemble(0, 0x0000, 0x70a, "ErrorHandler");

				// Mouse handler referenced by Seg0, Fn92
				mainProgram.Disassemble(0, 0x0000, 0x17cd, "MouseHandler");

				// Referenced by Seg6; Pointers to functions in Seg6
				mainProgram.Disassemble(0, 0x0860, 0x1139, null);
				mainProgram.Disassemble(0, 0x0860, 0x1250, null);
				mainProgram.Disassemble(0, 0x0860, 0x132a, null);
				mainProgram.Disassemble(0, 0x0860, 0x1379, null);
				mainProgram.Disassemble(0, 0x0860, 0x13c8, null);
				mainProgram.Disassemble(0, 0x0860, 0x1472, null);
				mainProgram.Disassemble(0, 0x0860, 0x15e0, null);
				mainProgram.Disassemble(0, 0x0860, 0x1646, null);

				// Referenced by Seg7, Fn2; Pointers to functions in Seg6
				mainProgram.Disassemble(0, 0x0860, 0x1613, null);

				// Referenced by Seg12, Fn1; Pointers to functions in Seg6
				mainProgram.Disassemble(0, 0x0860, 0x0ee0, null);
				mainProgram.Disassemble(0, 0x0860, 0x1563, null);

				break;

			case 2:
				// Timer handler referenced by Seg0, Fn1
				mainProgram.Disassemble(0, 0x0000, 0x199, "TimerHandler");

				// Error handler referenced by Seg0, Fn23
				mainProgram.Disassemble(0, 0x0000, 0x70a, "ErrorHandler");

				// Mouse handler referenced by Seg0, Fn92
				mainProgram.Disassemble(0, 0x0000, 0x17cd, "MouseHandler");

				// Referenced by Seg6; Pointers to functions in Seg6
				mainProgram.Disassemble(0, 0x0860, 0x1139, null);
				mainProgram.Disassemble(0, 0x0860, 0x1250, null);
				mainProgram.Disassemble(0, 0x0860, 0x132a, null);
				mainProgram.Disassemble(0, 0x0860, 0x1379, null);
				mainProgram.Disassemble(0, 0x0860, 0x13c8, null);
				mainProgram.Disassemble(0, 0x0860, 0x1472, null);
				mainProgram.Disassemble(0, 0x0860, 0x15e0, null);
				mainProgram.Disassemble(0, 0x0860, 0x1646, null);

				// Referenced by Seg7, Fn2; Pointers to functions in Seg6
				mainProgram.Disassemble(0, 0x0860, 0x1613, null);

				// Referenced by Seg12, Fn1; Pointers to functions in Seg6
				mainProgram.Disassemble(0, 0x0860, 0x0ee0, null);
				mainProgram.Disassemble(0, 0x0860, 0x1563, null);

				break;

			case 3:
				// Timer handler referenced by Seg0, Fn1
				mainProgram.Disassemble(0, 0x0000, 0x1a7, "TimerHandler");

				// Error handler referenced by Seg0, Fn23
				mainProgram.Disassemble(0, 0x0000, 0x718, "ErrorHandler");

				// Mouse handler referenced by Seg0, Fn92
				mainProgram.Disassemble(0, 0x0000, 0x17db, "MouseHandler");

				// Referenced by Seg6; Pointers to functions in Seg6
				mainProgram.Disassemble(0, 0x07e6, 0x1165, null);
				mainProgram.Disassemble(0, 0x07e6, 0x127c, null);
				mainProgram.Disassemble(0, 0x07e6, 0x1356, null);
				mainProgram.Disassemble(0, 0x07e6, 0x13a5, null);
				mainProgram.Disassemble(0, 0x07e6, 0x13f4, null);
				mainProgram.Disassemble(0, 0x07e6, 0x149e, null);
				mainProgram.Disassemble(0, 0x07e6, 0x160c, null);
				mainProgram.Disassemble(0, 0x07e6, 0x1672, null);

				// Referenced by Seg7, Fn2; Pointers to functions in Seg6
				mainProgram.Disassemble(0, 0x07e6, 0x163f, null);

				// Referenced by Seg12, Fn1; Pointers to functions in Seg6
				mainProgram.Disassemble(0, 0x07e6, 0x0f0c, null);
				mainProgram.Disassemble(0, 0x07e6, 0x158f, null);

				// Referenced by Seg19, Fn10
				mainProgram.Disassemble(0, 0x1f20, 0x063a, null);

				break;

			case 4:
				// Timer handler referenced by Seg0, Fn1
				mainProgram.Disassemble(0, 0x0000, 0x1a7, "TimerHandler");

				// Error handler referenced by Seg0, Fn23
				mainProgram.Disassemble(0, 0x0000, 0x718, "ErrorHandler");

				// Mouse handler referenced by Seg0, Fn92
				mainProgram.Disassemble(0, 0x0000, 0x17db, "MouseHandler");

				// Referenced by Seg6; Pointers to functions in Seg6
				mainProgram.Disassemble(0, 0x07e6, 0x1165, null);
				mainProgram.Disassemble(0, 0x07e6, 0x127c, null);
				mainProgram.Disassemble(0, 0x07e6, 0x1356, null);
				mainProgram.Disassemble(0, 0x07e6, 0x13a5, null);
				mainProgram.Disassemble(0, 0x07e6, 0x13f4, null);
				mainProgram.Disassemble(0, 0x07e6, 0x149e, null);
				mainProgram.Disassemble(0, 0x07e6, 0x160c, null);
				mainProgram.Disassemble(0, 0x07e6, 0x1672, null);

				// Referenced by Seg7, Fn2; Pointers to functions in Seg6
				mainProgram.Disassemble(0, 0x07e6, 0x163f, null);

				// Referenced by Seg12, Fn1; Pointers to functions in Seg6
				mainProgram.Disassemble(0, 0x07e6, 0x0f0c, null);
				mainProgram.Disassemble(0, 0x07e6, 0x158f, null);

				// Referenced by Seg19, Fn10
				mainProgram.Disassemble(0, 0x1f20, 0x0640, null);

				break;

			case 5:
				// Timer handler referenced by Seg0, Fn1
				mainProgram.Disassemble(0, 0x0000, 0x1a7, "TimerHandler");

				// Error handler referenced by Seg0, Fn23
				mainProgram.Disassemble(0, 0x0000, 0x718, "ErrorHandler");

				// Mouse handler referenced by Seg0, Fn92
				mainProgram.Disassemble(0, 0x0000, 0x17db, "MouseHandler");

				// Referenced by Seg6; Pointers to functions in Seg6
				mainProgram.Disassemble(0, 0x0866, 0x1169, null);
				mainProgram.Disassemble(0, 0x0866, 0x1280, null);
				mainProgram.Disassemble(0, 0x0866, 0x135a, null);
				mainProgram.Disassemble(0, 0x0866, 0x13a9, null);
				mainProgram.Disassemble(0, 0x0866, 0x13f8, null);
				mainProgram.Disassemble(0, 0x0866, 0x14a2, null);
				mainProgram.Disassemble(0, 0x0866, 0x1560, null);
				mainProgram.Disassemble(0, 0x0866, 0x1610, null);
				mainProgram.Disassemble(0, 0x0866, 0x1676, null);

				// Referenced by Seg7, Fn2; Pointers to functions in Seg6
				mainProgram.Disassemble(0, 0x0866, 0x1643, null);

				// Regerenced by Seg12, Fn1; Pointers to functions in Seg6
				mainProgram.Disassemble(0, 0x0866, 0x0f10, null);
				mainProgram.Disassemble(0, 0x0866, 0x1593, null);

				// Referenced by Seg19, Fn10
				mainProgram.Disassemble(0, 0x1fa1, 0x0644, null);

				break;
		}

		Console.WriteLine("* Writing code");

		StreamWriter objectWriter = new($"{codePath}{Path.DirectorySeparatorChar}ObjectsAsm.cs");
		StreamWriter initWriter = new($"{codePath}{Path.DirectorySeparatorChar}InitsAsm.cs");
		StreamWriter getWriter = new($"{codePath}{Path.DirectorySeparatorChar}GettersAsm.cs");

		mainProgram.AssignOrdinals();

		uint[] segmentOffsets = mainProgram.Segments.Keys.ToArray();

		Array.Sort(segmentOffsets);

		// emit Main code
		for (int i = 0; i < segmentOffsets.Length; i++)
		{
			ProgramSegment segment = mainProgram.Segments.GetValueByKey(segmentOffsets[i]);

			segment.WriteAsmCS(codePath, 0);

			objectWriter.WriteLine($"private {segment.Name} o{segment.Name};");
			initWriter.WriteLine($"this.o{segment.Name} = new {segment.Name}(this);");
			getWriter.WriteLine($"public {segment.Name} {segment.Name}");
			getWriter.WriteLine("{");
			getWriter.WriteLine($"\tget {{ return this.o{segment.Name};}}");
			getWriter.WriteLine("}");
			getWriter.WriteLine();
		}

		
		Console.WriteLine("* Emitting flow graphs");
		// Emit flow graphs
		uint ilSegment = 0x181;
		ushort ilFunction = 0x14a;

		if (mainProgram.Segments.ContainsKey(ilSegment))
		{
			Console.WriteLine("Segment {0} exists", ilSegment);
			ProgramSegment segment = mainProgram.Segments.GetValueByKey(ilSegment);

			if (segment.Functions.ContainsKey(ilFunction))
			{
				function = segment.Functions.GetValueByKey(ilFunction);

				if (function.FlowGraph != null)
				{
					function.FlowGraph.WriteGraphDOT("test.gv");
					Console.WriteLine("Func {0} exists, writed test.gv", ilFunction);
				}
            }
		}

		//Console.WriteLine();
		Console.WriteLine("* Translating code to IL");
		if (mainProgram.Segments.ContainsKey(0x181))
		{
			Console.WriteLine("Segment 0x181 exists for IL");
			ProgramSegment segment = mainProgram.Segments.GetValueByKey(0x181);
			Console.WriteLine("Segment 0x181 has {0} functions", segment.Functions.Count);
			for (int i = 0; i < segment.Functions.Count; i++)
			{
				//segment.Functions[i].Value.TranslateToIL();
			}
		}

		// Misc Driver
		ProcessOverlay("Civ1", "Misc", "MISC.EXE", "Misc", version, codePath);

		// VGA Driver
		ProcessOverlay("Civ1", "VGA", "MGRAPHIC.EXE", "VGA", version, codePath);

		// Sound Drivers
		TranslateOverlayToIL(ProcessOverlay("Civ1", "NSound", "NSOUND.CVL", "NSnd", version, codePath), "NSound", codePath);
		TranslateOverlayToIL(ProcessOverlay("Civ1", "ASound", "ASOUND.CVL", "ASnd", version, codePath), "ASound", codePath);
		TranslateOverlayToIL(ProcessOverlay("Civ1", "ISound", "ISOUND.CVL", "ISnd", version, codePath), "ISound", codePath);
		TranslateOverlayToIL(ProcessOverlay("Civ1", "RSound", "RSOUND.CVL", "RSnd", version, codePath), "RSound", codePath);
		TranslateOverlayToIL(ProcessOverlay("Civ1", "TSound", "TSOUND.CVL", "TSnd", version, codePath), "TSound", codePath);

		getWriter.Close();
		initWriter.Close();
		objectWriter.Close();

		Console.WriteLine("Processing finished");
	}

	/// <summary>
	/// Disassembles a single overlay driver and emits its segments as assembly level C#.
	/// </summary>
	/// <param name="name">Driver name, used for the console messages</param>
	/// <param name="fileName">Overlay file name inside GameSRC\Civ1</param>
	/// <param name="suffix">Suffix appended to the emitted segment names</param>
	private static Disassembler.MainProgram? ProcessOverlay(string gameDir, string name, string fileName, string suffix, int version, string codePath)
	{
		Console.WriteLine("* Processing overlay {0}", name);

		try
		{
			MZExecutable overlayEXE = new(string.Format("GameSRC{0}{1}{0}{2}",
				Path.DirectorySeparatorChar, gameDir, fileName));

			List<ModuleMatch> matches = [];
			Disassembler.MainProgram disassembler = new Disassembler.MainProgram(overlayEXE, matches, version);

			disassembler.DisassembleOverlay();
			disassembler.AssignOrdinals();

			uint[] segmentOffsets = disassembler.Segments.Keys.ToArray();
			Array.Sort(segmentOffsets);

			for (int i = 0; i < segmentOffsets.Length; i++)
			{
				ProgramSegment segment = disassembler.Segments.GetValueByKey(segmentOffsets[i]);
				segment.Name += suffix;
				segment.WriteAsmCS(codePath, 1);
			}

			Console.WriteLine("{0} segments: {1}", name, segmentOffsets.Length);

			return disassembler;
		}
		catch (Exception ex)
		{
			Console.WriteLine($"Warning: {name} overlay disassembly failed: {ex.Message}");

			return null;
		}
	}

	/// <summary>
	/// Translates every function of an already disassembled overlay to IL and dumps the result.
	/// Functions that the IL translator can't handle yet are reported instead of aborting the run.
	/// </summary>
	/// <param name="program">Disassembled overlay, or null when the disassembly failed</param>
	/// <param name="name">Driver name, used for the console messages and the output file</param>
	private static void TranslateOverlayToIL(Disassembler.MainProgram? program, string name, string codePath)
	{
		if (program == null)
			return;

		Console.WriteLine("* Translating overlay {0} to IL", name);

		int translated = 0;
		int failed = 0;
		Dictionary<string, int> reasons = [];

		using StreamWriter writer = new($"{codePath}{Path.DirectorySeparatorChar}{name}IL.txt");

		uint[] segmentOffsets = program.Segments.Keys.ToArray();
		Array.Sort(segmentOffsets);

		for (int i = 0; i < segmentOffsets.Length; i++)
		{
			ProgramSegment segment = program.Segments.GetValueByKey(segmentOffsets[i]);

			for (int j = 0; j < segment.Functions.Count; j++)
			{
				ProgramFunction function = segment.Functions[j].Value;

				if (function.FlowGraph == null)
					continue;

				try
				{
					function.FlowGraph.TranslateToIL();
				}
				catch (Exception ex)
				{
					failed++;
					CountFailure(reasons, ex, function.FlowGraph.LastTranslatedInstruction);

					writer.WriteLine($"// {segment.Name}.{function.Name} - NOT TRANSLATED at " +
						$"'{function.FlowGraph.LastTranslatedInstruction}': {ex.Message}");

					// a translator that says "not implemented" is expected, anything else is a bug
					if (ex.GetType() != typeof(Exception))
					{
						writer.WriteLine($"// {ex.StackTrace?.Replace("\n", "\n// ")}");
					}

					writer.WriteLine();
					continue;
				}

				// the dump can fail on its own, an IL expression that can't be written out
				// is not a translation that we can count as successful
				try
				{
					WriteFunctionIL(writer, segment, function);
					translated++;
				}
				catch (Exception ex)
				{
					failed++;
					CountFailure(reasons, ex);

					writer.WriteLine($"// {segment.Name}.{function.Name} - NOT WRITTEN: {ex.Message}");
					writer.WriteLine();
				}
			}
		}

		Console.WriteLine("{0} IL: {1} of {2} functions translated", name, translated, translated + failed);

		foreach (KeyValuePair<string, int> reason in reasons.OrderByDescending(item => item.Value))
		{
			Console.WriteLine("  {0}x {1}", reason.Value, reason.Key);
		}
	}

	/// <summary>
	/// Groups a translation failure by kind, the quoted parts are the specific register or parameter.
	/// The lookbehind keeps apostrophes inside words ("Don't") from opening a quote.
	/// </summary>
	private static void CountFailure(Dictionary<string, int> reasons, Exception ex, CPUInstruction? instruction = null)
	{
		string reason = Regex.Replace(ex.Message, "(?<![A-Za-z])'[^']*'", "'?'");

		if (instruction != null)
		{
			reason = $"{instruction.InstructionType} - {reason}";
		}

		reasons.TryGetValue(reason, out int count);
		reasons[reason] = count + 1;
	}

	/// <summary>
	/// Dumps the IL instructions of a translated function, one flow graph node at a time.
	/// </summary>
	private static void WriteFunctionIL(StreamWriter writer, ProgramSegment segment, ProgramFunction function)
	{
		writer.WriteLine($"// {segment.Name}.{function.Name}");

		uint[] nodeAddresses = function.FlowGraph!.Nodes.Keys.ToArray();
		Array.Sort(nodeAddresses);

		for (int i = 0; i < nodeAddresses.Length; i++)
		{
			FlowGraphNode node = function.FlowGraph.Nodes.GetValueByKey(nodeAddresses[i]);

			if (node.ILInstructions.Count == 0)
				continue;

			writer.WriteLine($"n{node.LinearAddress:x}:");

			for (int j = 0; j < node.ILInstructions.Count; j++)
			{
				writer.WriteLine($"\t{node.ILInstructions[j].ToCSString()};");
			}
		}

		writer.WriteLine();
	}
}