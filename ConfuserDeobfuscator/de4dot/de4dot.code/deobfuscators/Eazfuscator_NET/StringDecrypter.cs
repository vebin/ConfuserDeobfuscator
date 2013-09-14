﻿/*
    Copyright (C) 2011-2013 de4dot@gmail.com

    This file is part of de4dot.

    de4dot is free software: you can redistribute it and/or modify
    it under the terms of the GNU General Public License as published by
    the Free Software Foundation, either version 3 of the License, or
    (at your option) any later version.

    de4dot is distributed in the hope that it will be useful,
    but WITHOUT ANY WARRANTY; without even the implied warranty of
    MERCHANTABILITY or FITNESS FOR A PARTICULAR PURPOSE.  See the
    GNU General Public License for more details.

    You should have received a copy of the GNU General Public License
    along with de4dot.  If not, see <http://www.gnu.org/licenses/>.
*/

using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using dnlib.DotNet;
using dnlib.DotNet.Emit;
using de4dot.blocks;
using de4dot.blocks.cflow;

namespace de4dot.code.deobfuscators.Eazfuscator_NET {
	class StringDecrypter {
		ModuleDefMD module;
		TypeDef stringType;
		MethodDef stringMethod;
		TypeDef dataDecrypterType;
		short s1, s2, s3;
		int i1, i2, i3, i4, i5, i6;
		bool checkMinus2;
		bool usePublicKeyToken;
		int keyLen;
		byte[] theKey;
		int magic1;
		uint rldFlag, bytesFlag;
		EmbeddedResource encryptedResource;
		BinaryReader reader;
		DecrypterType decrypterType;
		StreamHelperType streamHelperType;
		EfConstantsReader stringMethodConsts;
		bool isV32OrLater;
		int? validStringDecrypterValue;
		Dynocode dynocode;

		class StreamHelperType {
			public TypeDef type;
			public MethodDef readInt16Method;
			public MethodDef readInt32Method;
			public MethodDef readBytesMethod;

			public bool Detected {
				get {
					return readInt16Method != null &&
						  readInt32Method != null &&
						  readBytesMethod != null;
				}
			}

			public StreamHelperType(TypeDef type) {
				this.type = type;

				foreach (var method in type.Methods) {
					if (method.IsStatic || method.Body == null || method.IsPrivate || method.GenericParameters.Count > 0)
						continue;
					if (DotNetUtils.isMethod(method, "System.Int16", "()"))
						readInt16Method = method;
					else if (DotNetUtils.isMethod(method, "System.Int32", "()"))
						readInt32Method = method;
					else if (DotNetUtils.isMethod(method, "System.Byte[]", "(System.Int32)"))
						readBytesMethod = method;
				}
			}
		}

		public int? ValidStringDecrypterValue {
			get { return validStringDecrypterValue;}
		}

		public TypeDef Type {
			get { return stringType; }
		}

		public EmbeddedResource Resource {
			get { return encryptedResource; }
		}

		public IEnumerable<TypeDef> Types {
			get {
				return new List<TypeDef> {
					stringType,
					dataDecrypterType,
				};
			}
		}

		public IEnumerable<TypeDef> DynocodeTypes {
			get { return dynocode.Types; }
		}

		public MethodDef Method {
			get { return stringMethod; }
		}

		public bool Detected {
			get { return stringType != null; }
		}

		public StringDecrypter(ModuleDefMD module, DecrypterType decrypterType) {
			this.module = module;
			this.decrypterType = decrypterType;
		}

		static bool checkIfV32OrLater(TypeDef type) {
			int numInts = 0;
			foreach (var field in type.Fields) {
				if (field.FieldSig.GetFieldType().GetElementType() == ElementType.I4)
					numInts++;
			}
			return numInts >= 2;
		}

		public void find() {
			foreach (var type in module.Types) {
				if (!checkType(type))
					continue;

				foreach (var method in type.Methods) {
					if (!checkDecrypterMethod(method))
						continue;

					stringType = type;
					stringMethod = method;
					isV32OrLater = checkIfV32OrLater(stringType);
					return;
				}
			}
		}

		static string[] requiredFieldTypes = new string[] {
			"System.Byte[]",
			"System.Int16",
		};
		bool checkType(TypeDef type) {
			if (!new FieldTypes(type).all(requiredFieldTypes))
				return false;
			if (type.NestedTypes.Count == 0) {
				return DotNetUtils.findFieldType(type, "System.IO.BinaryReader", true) != null &&
					DotNetUtils.findFieldType(type, "System.Collections.Generic.Dictionary`2<System.Int32,System.String>", true) != null;
			}
			else if (type.NestedTypes.Count == 3) {
				streamHelperType = findStreamHelperType(type);
				return streamHelperType != null;
			}
			else if (type.NestedTypes.Count == 1) {
				return type.NestedTypes[0].IsEnum;
			}
			else
				return false;
		}

		static string[] streamHelperTypeFields = new string[] {
			"System.IO.Stream",
			"System.Byte[]",
		};
		static StreamHelperType findStreamHelperType(TypeDef type) {
			foreach (var field in type.Fields) {
				var nested = field.FieldSig.GetFieldType().TryGetTypeDef();
				if (nested == null)
					continue;
				if (nested.DeclaringType != type)
					continue;
				if (!new FieldTypes(nested).exactly(streamHelperTypeFields))
					continue;
				var streamHelperType = new StreamHelperType(nested);
				if (!streamHelperType.Detected)
					continue;

				return streamHelperType;
			}
			return null;
		}

		static string[] requiredLocalTypes = new string[] {
			"System.Boolean",
			"System.Byte[]",
			"System.Char[]",
			"System.Int16",
			"System.Int32",
			"System.Reflection.Assembly",
			"System.String",
		};
		static bool checkDecrypterMethod(MethodDef method) {
			if (method == null || !method.IsStatic || method.Body == null)
				return false;
			if (!DotNetUtils.isMethod(method, "System.String", "(System.Int32)"))
				return false;
			if (!new LocalTypes(method).all(requiredLocalTypes))
				return false;

			foreach (var instr in method.Body.Instructions) {
				if (instr.OpCode != OpCodes.Callvirt)
					continue;
				var calledMethod = instr.Operand as IMethod;
				if (calledMethod != null && calledMethod.FullName == "System.IO.Stream System.Reflection.Assembly::GetManifestResourceStream(System.String)")
					return true;
			}
			return false;
		}

		public void initialize(ISimpleDeobfuscator simpleDeobfuscator) {
			if (stringType == null)
				return;

			if (!findConstants(simpleDeobfuscator)) {
				if (encryptedResource == null)
					Logger.w("Could not find encrypted resource. Strings cannot be decrypted.");
				else
					Logger.w("Can't decrypt strings. Possibly a new Eazfuscator.NET version.");
				return;
			}
		}

		bool findConstants(ISimpleDeobfuscator simpleDeobfuscator) {
			dynocode = new Dynocode(simpleDeobfuscator);
			simpleDeobfuscator.deobfuscate(stringMethod);
			stringMethodConsts = new EfConstantsReader(stringMethod);

			if (!findResource(stringMethod))
				return false;

			checkMinus2 = isV32OrLater || DeobUtils.hasInteger(stringMethod, -2);
			usePublicKeyToken = callsGetPublicKeyToken(stringMethod);

			var int64Method = findInt64Method(stringMethod);
			if (int64Method != null)
				decrypterType.Type = int64Method.DeclaringType;

			if (!findShorts())
				return false;
			if (!findInt3())
				return false;
			if (!findInt4())
				return false;
			if (checkMinus2 && !findInt5())
				return false;
			dataDecrypterType = findDataDecrypterType(stringMethod);
			if (dataDecrypterType == null)
				return false;

			if (isV32OrLater) {
				bool initializedAll;
				int index = findInitIntsIndex(stringMethod, out initializedAll);

				var cctor = stringType.FindStaticConstructor();
				if (!initializedAll && cctor != null) {
					simpleDeobfuscator.deobfuscate(cctor);
					if (!findIntsCctor(cctor))
						return false;
				}

				if (decrypterType.Detected && !decrypterType.initialize())
					return false;

				if (!findInts(index))
					return false;
			}

			initializeFlags();
			initialize();

			return true;
		}

		void initializeFlags() {
			if (!isV32OrLater) {
				rldFlag = 0x40000000;
				bytesFlag = 0x80000000;
				return;
			}

			var instrs = stringMethod.Body.Instructions;
			for (int i = 0; i < instrs.Count; i++) {
				var ldci4 = instrs[i];
				if (!stringMethodConsts.isLoadConstantInt32(ldci4))
					continue;
				int index = i, tmp;
				if (!stringMethodConsts.getInt32(ref index, out tmp) || !isFlagsMask(tmp))
					continue;
				if (findFlags(i))
					return;
			}

			throw new ApplicationException("Could not find string decrypter flags");
		}

		static bool isFlagsMask(int value) {
			return value == 0x1FFFFFFF || value == 0x0FFFFFFF;
		}

		class FlagsInfo {
			public Local Local { get; set; }
			public uint Value { get; set; }
			public int Offset { get; set; }
			public FlagsInfo(Local local, uint value, int offset) {
				Local = local;
				Value = value;
				Offset = offset;
			}
		}

		bool findFlags(int index) {
			var flags = findFlags2(index);
			if (flags == null)
				return false;

			flags.Sort((a, b) => a.Offset.CompareTo(b.Offset));

			rldFlag = flags[0].Value;
			bytesFlag = flags[1].Value;
			return true;
		}

		List<FlagsInfo> findFlags2(int index) {
			var flags = new List<FlagsInfo>(3);
			for (int i = index - 1; i >= 0; i--) {
				var instr = stringMethod.Body.Instructions[i];
				if (instr.OpCode.FlowControl != FlowControl.Next)
					break;
				if (!stringMethodConsts.isLoadConstantInt32(instr))
					continue;
				int index2 = i, value;
				if (!stringMethodConsts.getInt32(ref index2, out value))
					continue;
				if ((uint)value != 0x80000000 && value != 0x40000000 && value != 0x20000000)
					continue;
				var local = getFlagsLocal(stringMethod, index2);
				if (local == null)
					continue;
				int offset = getFlagsOffset(stringMethod, index2, local);
				if (offset < 0)
					continue;

				flags.Add(new FlagsInfo(local, (uint)value, offset));
				if (flags.Count != 3)
					continue;

				return flags;
			}

			return null;
		}

		static int getFlagsOffset(MethodDef method, int index, Local local) {
			var instrs = method.Body.Instructions;
			for (; index < instrs.Count; index++) {
				var ldloc = instrs[index];
				if (!ldloc.IsLdloc())
					continue;
				if (ldloc.GetLocal(method.Body.Variables) != local)
					continue;

				return index;
			}
			return -1;
		}

		static Local getFlagsLocal(MethodDef method, int index) {
			var instrs = method.Body.Instructions;
			if (index + 5 >= instrs.Count)
				return null;
			if (instrs[index++].OpCode.Code != Code.And)
				return null;
			if (instrs[index++].OpCode.Code != Code.Ldc_I4_0)
				return null;
			if (instrs[index++].OpCode.Code != Code.Ceq)
				return null;
			if (instrs[index++].OpCode.Code != Code.Ldc_I4_0)
				return null;
			if (instrs[index++].OpCode.Code != Code.Ceq)
				return null;
			var stloc = instrs[index++];
			if (!stloc.IsStloc())
				return null;
			return stloc.GetLocal(method.Body.Variables);
		}

		void initialize() {
			reader = new BinaryReader(encryptedResource.GetResourceStream());
			short len = (short)(reader.ReadInt16() ^ s1);
			if (len != 0)
				theKey = reader.ReadBytes(len);
			else
				keyLen = reader.ReadInt16() ^ s2;
		}

		public string decrypt(int val) {
			validStringDecrypterValue = val;
			while (true) {
				int offset = magic1 ^ i3 ^ val ^ i6;
				reader.BaseStream.Position = offset;
				byte[] tmpKey;
				if (theKey == null) {
					tmpKey = reader.ReadBytes(keyLen == -1 ? (short)(reader.ReadInt16() ^ s3 ^ offset) : keyLen);
					if (isV32OrLater) {
						for (int i = 0; i < tmpKey.Length; i++)
							tmpKey[i] ^= (byte)(magic1 >> ((i & 3) << 3));
					}
				}
				else
					tmpKey = theKey;

				int flags = i4 ^ magic1 ^ offset ^ reader.ReadInt32();
				if (checkMinus2 && flags == -2) {
					var ary2 = reader.ReadBytes(4);
					val = -(magic1 ^ i5) ^ (ary2[2] | (ary2[0] << 8) | (ary2[3] << 16) | (ary2[1] << 24));
					continue;
				}

				var bytes = reader.ReadBytes(flags & 0x1FFFFFFF);
				decrypt1(bytes, tmpKey);
				var pkt = PublicKeyBase.ToPublicKeyToken(module.Assembly.PublicKey);
				if (usePublicKeyToken && !PublicKeyBase.IsNullOrEmpty2(pkt)) {
					for (int i = 0; i < bytes.Length; i++)
						bytes[i] ^= (byte)((pkt.Data[i & 7] >> 5) + (pkt.Data[i & 7] << 3));
				}

				if ((flags & rldFlag) != 0)
					bytes = rld(bytes);
				if ((flags & bytesFlag) != 0) {
					var sb = new StringBuilder(bytes.Length);
					foreach (var b in bytes)
						sb.Append((char)b);
					return sb.ToString();
				}
				else
					return Encoding.Unicode.GetString(bytes);
			}
		}

		static byte[] rld(byte[] src) {
			var dst = new byte[src[2] + (src[3] << 8) + (src[0] << 16) + (src[1] << 24)];
			int srcIndex = 4;
			int dstIndex = 0;
			int flags = 0;
			int bit = 128;
			while (dstIndex < dst.Length) {
				bit <<= 1;
				if (bit == 256) {
					bit = 1;
					flags = src[srcIndex++];
				}

				if ((flags & bit) == 0) {
					dst[dstIndex++] = src[srcIndex++];
					continue;
				}

				int numBytes = (src[srcIndex] >> 2) + 3;
				int copyIndex = dstIndex - ((src[srcIndex + 1] + (src[srcIndex] << 8)) & 0x3FF);
				if (copyIndex < 0)
					break;
				while (dstIndex < dst.Length && numBytes-- > 0)
					dst[dstIndex++] = dst[copyIndex++];
				srcIndex += 2;
			}

			return dst;
		}

		static void decrypt1(byte[] dest, byte[] key) {
			byte b = (byte)((key[1] + 7) ^ (dest.Length + 11));
			uint lcg = (uint)((key[0] | (key[2] << 8)) + (b << 3));
			b += 3;
			ushort xn = 0;
			for (int i = 0; i < dest.Length; i++) {
				if ((i & 1) == 0) {
					lcg = lcgNext(lcg);
					xn = (ushort)(lcg >> 16);
				}
				byte tmp = dest[i];
				dest[i] ^= (byte)(key[1] ^ xn ^ b);
				b = (byte)(tmp + 3);
				xn >>= 8;
			}
		}

		static uint lcgNext(uint lcg) {
			return lcg * 214013 + 2531011;
		}

		bool findResource(MethodDef method) {
			encryptedResource = findResourceFromCodeString(method) ??
								findResourceFromStringBuilder(method);
			return encryptedResource != null;
		}

		EmbeddedResource findResourceFromCodeString(MethodDef method) {
			return DotNetUtils.getResource(module, DotNetUtils.getCodeStrings(method)) as EmbeddedResource;
		}

		EmbeddedResource findResourceFromStringBuilder(MethodDef method) {
			int startIndex = EfUtils.findOpCodeIndex(method, 0, Code.Newobj, "System.Void System.Text.StringBuilder::.ctor()");
			if (startIndex < 0)
				return null;
			int endIndex = EfUtils.findOpCodeIndex(method, startIndex, Code.Call, "System.String System.Text.StringBuilder::ToString()");
			if (endIndex < 0)
				return null;

			var sb = new StringBuilder();
			var instrs = method.Body.Instructions;
			int val = 0, shift = 0;
			for (int i = startIndex; i < endIndex; i++) {
				var instr = instrs[i];
				if (instr.OpCode.Code == Code.Call && instr.Operand.ToString() == "System.Text.StringBuilder System.Text.StringBuilder::Append(System.Char)") {
					sb.Append((char)(val >> shift));
					shift = 0;
				}
				if (stringMethodConsts.isLoadConstantInt32(instr)) {
					int tmp;
					if (!stringMethodConsts.getInt32(ref i, out tmp))
						break;
					if (i >= endIndex)
						break;

					var next = instrs[i];
					if (next.OpCode.Code == Code.Shr)
						shift = tmp;
					else {
						val = tmp;
						shift = 0;
					}
				}
			}

			return DotNetUtils.getResource(module, sb.ToString()) as EmbeddedResource;
		}

		static MethodDef findInt64Method(MethodDef method) {
			foreach (var instr in method.Body.Instructions) {
				if (instr.OpCode.Code != Code.Call)
					continue;
				var calledMethod = instr.Operand as MethodDef;
				if (calledMethod == null)
					continue;
				if (!DotNetUtils.isMethod(calledMethod, "System.Int64", "()"))
					continue;

				return calledMethod;
			}
			return null;
		}

		static TypeDef findDataDecrypterType(MethodDef method) {
			foreach (var instr in method.Body.Instructions) {
				if (instr.OpCode.Code != Code.Call)
					continue;
				var calledMethod = instr.Operand as MethodDef;
				if (calledMethod == null)
					continue;
				if (!DotNetUtils.isMethod(calledMethod, "System.Byte[]", "(System.Byte[],System.Byte[])"))
					continue;

				return calledMethod.DeclaringType;
			}
			return null;
		}

		bool findShorts() {
			int index = 0;
			if (!findShort(ref index, ref s1))
				return false;
			if (!findShort(ref index, ref s2))
				return false;
			if (!findShort(ref index, ref s3))
				return false;

			return true;
		}

		bool findShort(ref int index, ref short s) {
			if (!findCallReadInt16(ref index))
				return false;
			index++;
			return stringMethodConsts.getInt16(ref index, out s);
		}

		bool findInts(int index) {
			if (index < 0)
				return false;

			i2 = 0;
			var instrs = stringMethod.Body.Instructions;

			var emu = new InstructionEmulator(stringMethod);
			foreach (var kv in stringMethodConsts.Locals32)
				emu.setLocal(kv.Key, new Int32Value(kv.Value));

			var fields = new Dictionary<FieldDef, int?>();
			for (int i = index; i < instrs.Count - 2; i++) {
				var instr = instrs[i];

				FieldDef field;
				switch (instr.OpCode.Code) {
				case Code.Ldsfld:
					field = instr.Operand as FieldDef;
					if (field == null || field.DeclaringType != stringMethod.DeclaringType || field.FieldType.GetElementType() != ElementType.I4)
						goto default;
					fields[field] = null;
					emu.push(new Int32Value(i1));
					break;

				case Code.Stsfld:
					field = instr.Operand as FieldDef;
					if (field == null || field.DeclaringType != stringMethod.DeclaringType || field.FieldType.GetElementType() != ElementType.I4)
						goto default;
					if (fields.ContainsKey(field) && fields[field] == null)
						goto default;
					var val = emu.pop() as Int32Value;
					if (val == null || !val.allBitsValid())
						fields[field] = null;
					else
						fields[field] = val.value;
					break;

				case Code.Call:
					var method = instr.Operand as MethodDef;
					if (!decrypterType.Detected || method != decrypterType.Int64Method)
						goto done;
					emu.push(new Int64Value((long)decrypterType.getMagic()));
					break;

				case Code.Newobj:
					if (!emulateDynocode(emu, ref i))
						goto default;
					break;

				default:
					if (instr.OpCode.FlowControl != FlowControl.Next)
						goto done;
					emu.emulate(instr);
					break;
				}
			}
done: ;

			foreach (var val in fields.Values) {
				if (val == null)
					continue;
				magic1 = i2 = val.Value;
				return true;
			}

			return false;
		}

		bool emulateDynocode(InstructionEmulator emu, ref int index) {
			var instrs = stringMethod.Body.Instructions;
			var instr = instrs[index];

			var ctor = instr.Operand as MethodDef;
			if (ctor == null || ctor.MethodSig.GetParamCount() != 1 || ctor.MethodSig.Params[0].ElementType != ElementType.I4)
				return false;

			if (index + 4 >= instrs.Count)
				return false;
			var ldloc = instrs[index + 3];
			if (!ldloc.IsLdloc() || instrs[index + 4].OpCode.Code != Code.Stfld)
				return false;

			var initValue = emu.getLocal(ldloc.GetLocal(stringMethod.Body.Variables)) as Int32Value;
			if (initValue == null || !initValue.allBitsValid())
				return false;

			int leaveIndex = findLeave(instrs, index);
			if (leaveIndex < 0)
				return false;
			var afterLoop = instrs[leaveIndex].Operand as Instruction;
			if (afterLoop == null)
				return false;
			int newIndex = instrs.IndexOf(afterLoop);
			var loopLocal = getDCLoopLocal(index, newIndex);
			if (loopLocal == null)
				return false;
			var initValue2 = emu.getLocal(loopLocal) as Int32Value;
			if (initValue2 == null || !initValue2.allBitsValid())
				return false;

			var dcGen = dynocode.getDynocodeGenerator(ctor.DeclaringType);
			if (dcGen == null)
				return false;
			int loopLocalValue = initValue2.value;
			foreach (var val in dcGen.getValues(initValue.value))
				loopLocalValue ^= val;

			emu.setLocal(loopLocal, new Int32Value(loopLocalValue));
			emu.emulate(instr);
			index = newIndex - 1;
			return true;
		}

		Local getDCLoopLocal(int start, int end) {
			var instrs = stringMethod.Body.Instructions;
			for (int i = start; i < end - 1; i++) {
				if (instrs[i].OpCode.Code != Code.Xor)
					continue;
				var stloc = instrs[i + 1];
				if (!stloc.IsStloc())
					continue;
				return stloc.GetLocal(stringMethod.Body.Variables);
			}
			return null;
		}

		static int findLeave(IList<Instruction> instrs, int index) {
			for (int i = index; i < instrs.Count; i++) {
				if (instrs[i].OpCode.Code == Code.Leave_S || instrs[i].OpCode.Code == Code.Leave)
					return i;
			}
			return -1;
		}

		static int findInitIntsIndex(MethodDef method, out bool initializedAll) {
			initializedAll = false;

			var instrs = method.Body.Instructions;
			for (int i = 0; i < instrs.Count; i++) {
				var ldnull = instrs[i];
				if (ldnull.OpCode.Code != Code.Ldnull)
					continue;

				var stsfld = instrs[i + 1];
				if (stsfld.OpCode.Code != Code.Stsfld)
					continue;

				var storeField = stsfld.Operand as FieldDef;
				if (storeField == null || storeField.FieldType.FullName != "System.Byte[]")
					continue;

				var instr = instrs[i + 2];
				if (instr.OpCode.Code == Code.Ldsfld) {
					var loadField = instr.Operand as FieldDef;
					if (loadField == null || loadField.FieldType.GetElementType() != ElementType.I4)
						continue;
				}
				else if (instr.IsLdcI4()) {
					initializedAll = true;
				}
				else
					continue;

				return i;
			}

			return -1;
		}

		bool findIntsCctor(MethodDef cctor) {
			int index = 0;
			if (!findCallGetFrame(cctor, ref index))
				return findIntsCctor2(cctor);

			int tmp1, tmp2, tmp3 = 0;
			var constantsReader = new EfConstantsReader(cctor);
			if (!constantsReader.getNextInt32(ref index, out tmp1))
				return false;
			if (tmp1 == 0 && !constantsReader.getNextInt32(ref index, out tmp1))
				return false;
			if (!constantsReader.getNextInt32(ref index, out tmp2))
				return false;
			if (tmp2 == 0 && !constantsReader.getNextInt32(ref index, out tmp2))
				return false;

			index = 0;
			var instrs = cctor.Body.Instructions;
			while (index < instrs.Count) {
				int tmp4;
				if (!constantsReader.getNextInt32(ref index, out tmp4))
					break;
				if (index < instrs.Count && instrs[index].IsLdloc())
					tmp3 = tmp4;
			}

			i1 = tmp1 ^ tmp2 ^ tmp3;
			return true;
		}

		// Compact Framework doesn't have StackFrame
		bool findIntsCctor2(MethodDef cctor) {
			int index = 0;
			var instrs = cctor.Body.Instructions;
			var constantsReader = new EfConstantsReader(cctor);
			while (index >= 0) {
				int val;
				if (!constantsReader.getNextInt32(ref index, out val))
					break;
				if (index < instrs.Count && instrs[index].OpCode.Code == Code.Add) {
					i1 = val;
					return true;
				}
			}

			return false;
		}

		bool findInt3() {
			if (!isV32OrLater)
				return findInt3Old();
			return findInt3New();
		}

		// <= 3.1
		bool findInt3Old() {
			var instrs = stringMethod.Body.Instructions;
			for (int i = 0; i < instrs.Count - 4; i++) {
				var ldarg0 = instrs[i];
				if (ldarg0.OpCode.Code != Code.Ldarg_0)
					continue;

				var ldci4 = instrs[i + 1];
				if (!ldci4.IsLdcI4())
					continue;

				int index = i + 1;
				int value;
				if (!stringMethodConsts.getInt32(ref index, out value))
					continue;
				if (index >= instrs.Count)
					continue;

				if (instrs[index].OpCode.Code != Code.Xor)
					continue;

				i3 = value;
				return true;
			}

			return false;
		}

		// 3.2+
		bool findInt3New() {
			var instrs = stringMethod.Body.Instructions;
			for (int i = 0; i < instrs.Count; i++) {
				int index = i;

				var ldarg0 = instrs[index++];
				if (ldarg0.OpCode.Code != Code.Ldarg_0)
					continue;

				int value;
				if (!stringMethodConsts.getInt32(ref index, out value))
					continue;

				if (index + 3 >= instrs.Count)
					break;

				if (instrs[index++].OpCode.Code != Code.Xor)
					continue;

				if (!instrs[index++].IsLdloc())
					continue;

				if (instrs[index++].OpCode.Code != Code.Xor)
					continue;

				i3 = value;
				if (!findInt6(index++))
					return false;
				return true;
			}

			return false;
		}

		// v3.3.134+
		bool findInt6(int index) {
			index = getNextLdci4InSameBlock(index);
			if (index < 0)
				return true;

			return stringMethodConsts.getNextInt32(ref index, out i6);
		}

		bool findInt4() {
			int index = 0;
			if (!findCallReadInt32(ref index))
				return false;
			if (!stringMethodConsts.getNextInt32(ref index, out i4))
				return false;

			return true;
		}

		int getNextLdci4InSameBlock(int index) {
			var instrs = stringMethod.Body.Instructions;
			for (int i = index; i < instrs.Count; i++) {
				var instr = instrs[i];
				if (instr.OpCode.FlowControl != FlowControl.Next)
					return -1;
				if (stringMethodConsts.isLoadConstantInt32(instr))
					return i;
			}

			return -1;
		}

		bool findInt5() {
			int index = -1;
			while (true) {
				index++;
				if (!findCallReadBytes(ref index))
					return false;
				if (index <= 0)
					continue;
				var ldci4 = stringMethod.Body.Instructions[index - 1];
				if (!ldci4.IsLdcI4())
					continue;
				if (ldci4.GetLdcI4Value() != 4)
					continue;
				if (!stringMethodConsts.getNextInt32(ref index, out i5))
					return false;

				return true;
			}
		}

		static bool callsGetPublicKeyToken(MethodDef method) {
			int index = 0;
			return findCall(method, ref index, "System.Byte[] System.Reflection.AssemblyName::GetPublicKeyToken()");
		}

		bool findCallReadInt16(ref int index) {
			return findCall(stringMethod, ref index, streamHelperType == null ? "System.Int16 System.IO.BinaryReader::ReadInt16()" : streamHelperType.readInt16Method.FullName);
		}

		bool findCallReadInt32(ref int index) {
			return findCall(stringMethod, ref index, streamHelperType == null ? "System.Int32 System.IO.BinaryReader::ReadInt32()" : streamHelperType.readInt32Method.FullName);
		}

		bool findCallReadBytes(ref int index) {
			return findCall(stringMethod, ref index, streamHelperType == null ? "System.Byte[] System.IO.BinaryReader::ReadBytes(System.Int32)" : streamHelperType.readBytesMethod.FullName);
		}

		static bool findCallGetFrame(MethodDef method, ref int index) {
			return findCall(method, ref index, "System.Diagnostics.StackFrame System.Diagnostics.StackTrace::GetFrame(System.Int32)");
		}

		static bool findCall(MethodDef method, ref int index, string methodFullName) {
			for (; index < method.Body.Instructions.Count; index++) {
				if (!findCallvirt(method, ref index))
					return false;

				var calledMethod = method.Body.Instructions[index].Operand as IMethod;
				if (calledMethod == null)
					continue;
				if (calledMethod.ToString() != methodFullName)
					continue;

				return true;
			}
			return false;
		}

		static bool findCallvirt(MethodDef method, ref int index) {
			var instrs = method.Body.Instructions;
			for (; index < instrs.Count; index++) {
				var instr = instrs[index];
				if (instr.OpCode.Code != Code.Callvirt)
					continue;

				return true;
			}

			return false;
		}
	}
}