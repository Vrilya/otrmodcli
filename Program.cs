/* Licensed under the Open Software License version 3.0 */

using Ionic.Zip;
using OTRMod;
using OTRMod.OTR;
using OTRMod.ROM;
using OTRMod.Utility;

using System.Text;

static MemStream Run(string pathImage, bool romMode, bool calc, out string outName) {
	bool decompressOnly = romMode && calc;
	outName = "Decompressed.z64";
	try {
		byte[] iData = File.ReadAllBytes(pathImage);
		if (romMode)
			iData = Decompressor.Data(iData.ToBigEndian(), calc: calc);
		MemStream ms;
		if (decompressOnly)
			ms = new MemStream(iData);
		else {
			ms = new MemStream();
			ms.SetLength(0);
			ScriptParser sParser = new() {
				ScriptStrings = File.ReadAllLines(ReadPath("Script")),
				ImageData = iData
			};
			sParser.ParseScript();
			outName = sParser.OTRFileName;
			Generate.FromImage(ref ms);
		}

		return ms;
	}
	catch (Exception e) {
		Con.WriteLine(e);
		throw;
	}
}

static void TUIRun(bool romMode, bool calc) {
	using MemStream genMs = Run(ReadPath("Image"), romMode, calc, out string outName);
	genMs.Save(ReadPath($"Output", outName, false));
	genMs.Flush();
	genMs.Close();
#if NETCOREAPP1_0_OR_GREATER
	CompactAndCollect();
#endif
}

static void Extract(string? filePath = null, string? outDir = null) {
	Dictionary<string, Stream> OTRFiles = new();
	filePath ??= ReadPath("OTR");

	Con.WriteLine("Loading...");
	using FileStream fs = new(filePath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
	Load.From(fs, ref OTRFiles);
	outDir ??= ReadPath("Output", "Extracted", false);

	Con.WriteLine("Extracting...");
	foreach (KeyValuePair<string, Stream> file in OTRFiles)
		file.Value.Save(Path.Combine(outDir, EncodePath(file.Key)));

	OTRFiles.Clear();
	Con.WriteLine("Extraction complete.");
}

static void GenerateGeneric(string inputDir, string? outPath = null) {
	MemStream ms = new();
	ms.SetLength(0);
	Generate.FromImage(ref ms);

	if (outPath is null) {
		outPath = $"{Path.GetFileName(inputDir)}.otr";
		outPath = ReadPath($"Output", outPath, false);
	}

	ms.Save(outPath);
	ms.Flush();
	ms.Close();
}

static void AddSequence(string[] meta, string path, byte[] seqData) {
	string font = meta[1];
	if (font.StartsWith("0x")) {
#if NETCOREAPP3_0_OR_GREATER
		font = font[2..];
#else
		font = font.Substring(2);
#endif
	}
	else if (font == "-") {
		Warn($"Sequence uses custom Audiobank, skipping...", 2, path); /* FIXME */
		return;
	}
	else
		Warn("Audiobank index is expected to start with '0x'", 2, path);

	string? type = meta.GetIfExists(2);
	if (type is null || string.IsNullOrEmpty(type)) {
		Warn("No sequence type found, using 'bgm'", 3, path);
		type = "bgm";
	}
	else if (type.Contains(" ")) {
		Warn("Sequence type contains spaces", 3, path);
		type = type.Trim();
	}
	if (!type.ToLower().Equals(type)) {
		Warn("Sequence type has uppercase characters", 3, path);
		type = type.ToLower();
	}

	int cachePolicy = (type is "bgm" ? 2 : 1);
	if (!int.TryParse(font, System.Globalization.NumberStyles.AllowHexSpecifier, null, out int seqFont)) {
		Warn("Audiobank index couldn't be parsed as hex, using '0'", 2, path);
		seqFont = 0;
	}

	seqData = Format.Audio.ExportSeq(0, seqFont, cachePolicy, seqData);
	string name = meta[0].Replace('/', '|');
	path = $"custom/music/{name}_{type}";

	Generate.AddFile(path, seqData);
}

static void AutoGenerate(string? inputDir = null, string? outPath = null) {
	inputDir ??= ReadPath("Folder path", checkIfExists: false);
	string[] subFolders = Directory.GetDirectories(inputDir, "*", SearchOption.AllDirectories);

	foreach (string subFolder in subFolders) {
		List<string> files = new(Directory.GetFiles(subFolder));

		foreach (string file in files) {
			if (!file.EndsWith(".ootrs"))
				continue;

			using FileStream fs = new(file, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
			using ZipFile zip = ZipFile.Read(fs);

			MemStream seqMS = new();
			MemStream metaMS = new();
			string? metaPath = null;

			foreach (ZipEntry entry in zip.Entries) {
				if (entry.FileName.EndsWith(".zbank", StringComparison.OrdinalIgnoreCase)) {
					seqMS.SetLength(0);
					metaMS.SetLength(0);
					metaPath = null;
					Con.WriteLine($"Skipping {file}");
					break;
				}
				if (entry.FileName.EndsWith(".seq", StringComparison.OrdinalIgnoreCase)) {
					entry.Extract(seqMS);
				}
				if (entry.FileName.EndsWith(".meta", StringComparison.OrdinalIgnoreCase)) {
					entry.Extract(metaMS);
					metaPath = Path.Combine(file, entry.FileName);
				}
			}

			if (seqMS.Length == 0 || metaMS.Length == 0 || metaPath is null)
				continue;

			Con.WriteLine($"Adding {file}");

			string[] ootrsMetas = metaMS.ToArray().ToStringArray(Encoding.GetEncoding("iso-8859-1"));

			AddSequence(ootrsMetas, metaPath, seqMS.ToArray());
		}

		if (files.Find(a => a.Contains(".zbank")) is not null) {
			Con.WriteLine($"Skipping {subFolder}");
			continue;
		}

		string? seqFile = files.Find(a => a.Contains(".seq"));
		string? metaFile = files.Find(a => a.Contains(".meta"));

		if (seqFile is null || metaFile is null)
			continue;

		Con.WriteLine($"Adding {subFolder}");

		string[] metas = File.ReadAllLines(metaFile, Encoding.GetEncoding("iso-8859-1"));
		byte[] seq = File.ReadAllBytes(seqFile);

		AddSequence(metas, metaFile, seq);
	}

	GenerateGeneric(inputDir, outPath);
}

static void Build(string? inputDir = null, string? outPath = null) {
	inputDir ??= ReadPath("Input directory", checkIfExists: false);

	string[] filePaths = Directory.GetFiles(inputDir, "*", SearchOption.AllDirectories);

	foreach (string filePath in filePaths) {
#if NETCOREAPP3_0_OR_GREATER
		string newPath = filePath[inputDir.Length..].Replace(@"\", "/");
		if (newPath.StartsWith("/")) newPath = newPath[1..];
#else
		string newPath = filePath.Substring(inputDir.Length).Replace(@"\", "/");
		if (newPath.StartsWith("/")) newPath = newPath.Substring(1);
#endif
		byte[] fileBytes = File.ReadAllBytes(filePath);
		Generate.AddFile(DecodePath(newPath), fileBytes);
	}

	GenerateGeneric(inputDir, outPath);
}

if (args.Length > 0) {
	Con.Title = "OTRMod - Command Line Interface";

	string? otr = args.GetArg("extract", 'e');
	string? dir = args.GetArg("dir", 'd');

	if (otr == null && dir == null) {
		dir = args.GetArg("build", 'b');
		otr = args.GetArg("otr", 'o');

		bool mode = args.GetArgIndex("auto", 'a') != -1;

		if (otr == null && dir == null)
			return;

		if (mode is false)
			Build(dir, otr);
		else
			AutoGenerate(dir, otr);

	}
	else {
		Con.WriteLine($"{otr} {dir}");
		Extract(otr, dir);
	}

	return;
}

Dictionary<int, Action> options = new() {
	{ 1, () => TUIRun(true, false) },
	{ 2, () => TUIRun(false, false) },
	{ 3, () => TUIRun(true, true) },
	{ 4, () => Extract() },
	{ 5, () => Build() },
	{ 6, () => AutoGenerate() },
	{ 7, () => Exit(0) }
};

Con.WriteLine(Con.Title = "OTRMod - Console Mode");

do {
	Con.Write("\nSelect an option:\n" +
		"1. Create OTR mod from ROM (with auto-decompress)\n" +
		"2. Create OTR mod from file\n" +
		"3. Don't create OTR mod, just auto-decompress ROM\n" +
		"4. Extract OTR mod\n" +
		"5. Build OTR from folder\n" +
		"6. Build OTR from folder (auto)\n" +
		"7. Exit\n");

	if (!int.TryParse(Con.ReadLine(), out int choice)) {
		Con.WriteLine("Please enter a number.");
		continue;
	}
	if (!options.TryGetValue(choice, out Action? action)) {
		Con.WriteLine("Please select a valid option.");
		continue;
	}

	Con.WriteLine();
	action();
	Con.WriteLine();

	if (AnsweredYesTo("Do you want to start again?"))
		continue;
	Exit(0);

} while (true);