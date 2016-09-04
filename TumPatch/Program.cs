using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using System.Windows.Forms;
using Mono.Cecil;
using Mono.Cecil.Cil;

namespace TumPatch
{
    class Program
    {
        static void Expect(bool cond, string message)
        {
            if (!cond)
            {
                throw new Exception(message);
            }
        }

        static void ErrorMessageBox(string text)
        {
            MessageBox.Show(null, text, "Patcher", MessageBoxButtons.OK, MessageBoxIcon.Error);
        }

        static void InfoMessageBox(string text)
        {
            MessageBox.Show(null, text, "Patcher", MessageBoxButtons.OK, MessageBoxIcon.Information);
        }

        static void PatchSlowMenuTransitions(AssemblyDefinition asm)
        {
            var startGameIterator = asm.MainModule.GetType(@"StartGame/<Start>c__Iterator10");
            var moveNextMethod = startGameIterator.Methods.Single((md) => md.Name == "MoveNext");

            // ldc.r4 opcodes to replace.
            int[] instOffsets = {
                0x0062, 0x0129, 0x01b9, 0x0209,
                0x023a, 0x026c, 0x02be, 0x02ef,
                0x0321,
            };
            Array.Sort(instOffsets);
            int currInstIndex = 0;

            var instructions = moveNextMethod.Body.Instructions;
            for (int i = 0; i < instructions.Count; ++i)
            {
                var inst = instructions[i];
                Debug.WriteLine($"{i} offset = {inst.Offset}");
                if (inst.Offset != instOffsets[currInstIndex])
                    continue;

                Expect(inst.OpCode == OpCodes.Ldc_R4, "Expected instruction ldc_r4, but got " + inst.OpCode.ToString());

                instructions[i] = Instruction.Create(OpCodes.Ldc_R4, 0.0001f);
                ++currInstIndex;
                if (currInstIndex == instOffsets.Length)
                    break;
            }

            Expect(currInstIndex == instOffsets.Length, "Not all instructions were replaced, probably game was updated.");
        }

        static void PatchDialogueControls(AssemblyDefinition asm, AssemblyDefinition mscorlib, AssemblyDefinition unityEngineAsm, AssemblyDefinition unityEngineUIAsm)
        {
            var iter = asm.MainModule.GetType(@"Dialogue/<SlowText>c__IteratorD");
            var moveNextMethod = iter.Methods.Single((md) => md.Name == "MoveNext");

            var body = moveNextMethod.Body;
            var proc = body.GetILProcessor();

            var getKeyMethod = asm.MainModule.ImportReference(unityEngineAsm.MainModule.GetType("UnityEngine.Input").Methods.Single((m) => m.Name == "GetKey" && m.Parameters[0].ParameterType.Name == "KeyCode"));
            var getTextMethod = asm.MainModule.ImportReference(unityEngineUIAsm.MainModule.GetType("UnityEngine.UI.Text").Methods.Single((m) => m.Name == "get_text"));
            var setTextMethod = asm.MainModule.ImportReference(unityEngineUIAsm.MainModule.GetType("UnityEngine.UI.Text").Methods.Single((m) => m.Name == "set_text"));
            var stringConcatMethod = asm.MainModule.ImportReference(mscorlib.MainModule.GetType("System.String").Methods.Single((m) => m.Name == "Concat"
                && m.Parameters.Count == 2
                && m.Parameters[0].ParameterType.Name == "String"
                && m.Parameters[1].ParameterType.Name == "String"));

            var preReturnInst = body.Instructions[138]; // br 142
            //Expect(preReturnInst.OpCode == OpCodes.Br, "Expected instruction br (dialogue patch)");
            var pauseAfterCompleteInst = body.Instructions[101]; // ldarg.0
            Expect(pauseAfterCompleteInst.OpCode == OpCodes.Ldarg_0, "Expected instruction ldarg.0 (dialogue patch)");

            int skipDialogueItersInstrIndex = 141;
            Instruction[] skipDialogueInstrs =
            {
                // this.$current = null;
                // this.uiText.text = this.uiText.text + this.<t>__0;
                // this. t>__0 = "";
                // goto pauseDialogueAfterCompleteLabel;
                Instruction.Create(OpCodes.Ldarg_0),
                Instruction.Create(OpCodes.Ldnull),
                Instruction.Create(OpCodes.Stfld, iter.Fields.Single((f) => f.Name == "$current")),
                Instruction.Create(OpCodes.Ldarg_0),
                Instruction.Create(OpCodes.Ldfld, iter.Fields.Single((f) => f.Name == "uiText")),
                Instruction.Create(OpCodes.Dup),
                Instruction.Create(OpCodes.Callvirt, getTextMethod),
                Instruction.Create(OpCodes.Ldarg_0),
                Instruction.Create(OpCodes.Ldfld, iter.Fields.Single((f) => f.Name == "<t>__0")),
                Instruction.Create(OpCodes.Call, stringConcatMethod),
                Instruction.Create(OpCodes.Callvirt, setTextMethod),
                Instruction.Create(OpCodes.Ldarg_0),
                Instruction.Create(OpCodes.Ldstr, ""),
                Instruction.Create(OpCodes.Stfld, iter.Fields.Single((f) => f.Name == "<t>__0")),
                Instruction.Create(OpCodes.Br, pauseAfterCompleteInst)
            };

            Instruction skipDialogueLabel = skipDialogueInstrs[0];

            int skipDialogueRightShiftJumpInstrIndex = 68;
            Instruction[] skipDialogueRightShiftJumpInstrs = {
                // if (Input.GetKey(KeyCode.RightShift))
                //    goto skipDialogueIters;
                Instruction.Create(OpCodes.Ldc_I4, 303),
                Instruction.Create(OpCodes.Call, getKeyMethod),
                Instruction.Create(OpCodes.Brtrue, skipDialogueLabel)
            };

            proc.InsertAfter(body.Instructions[skipDialogueItersInstrIndex], skipDialogueLabel);
            for (int i = 1; i < skipDialogueInstrs.Length; ++i)
            {
                proc.InsertAfter(body.Instructions[i + skipDialogueItersInstrIndex], skipDialogueInstrs[i]);
            }
            for (int i = 0; i < skipDialogueRightShiftJumpInstrs.Length; ++i)
            {
                proc.InsertAfter(body.Instructions[i + skipDialogueRightShiftJumpInstrIndex], skipDialogueRightShiftJumpInstrs[i]);
            }
        }

        static int Main(string[] args)
        {

            if (args.Length == 0)
            {
                InfoMessageBox("Drop 'Assembly-CSharp.dll' from 'Steam/steamapps/The Underground Man/The undeground man_Data/Managed' folder to this .exe to patch the game.");
                return 1;
            }

            string assemblyPath = args[0];
            if (!File.Exists(assemblyPath))
            {
                ErrorMessageBox("Invalid file provided.");
                return 1;
            }

            AssemblyDefinition asm = null;
            string asmClonePath = null;

            try
            {
                asmClonePath = Path.GetTempFileName();
                File.Copy(assemblyPath, asmClonePath, true); // Cecil works only with files, eh.

                asm = AssemblyDefinition.ReadAssembly(asmClonePath);
            }
            catch (Exception e)
            {
                ErrorMessageBox($"Error opening '{ assemblyPath }': { e.Message }");
                if (asm != null) asm.Dispose();
                if (asmClonePath != null) File.Delete(asmClonePath);
                return 1;
            }

            string basePath = Path.GetDirectoryName(assemblyPath);

            AssemblyDefinition mscorlibAsm = AssemblyDefinition.ReadAssembly(Path.Combine(basePath, "mscorlib.dll"));
            AssemblyDefinition unityEngineAsm = AssemblyDefinition.ReadAssembly(Path.Combine(basePath, "UnityEngine.dll"));
            AssemblyDefinition unityEngineUIAsm = AssemblyDefinition.ReadAssembly(Path.Combine(basePath, "UnityEngine.UI.dll"));

            try
            {
                PatchSlowMenuTransitions(asm);
                PatchDialogueControls(asm, mscorlibAsm, unityEngineAsm, unityEngineUIAsm);
            }
            catch (Exception e)
            {
                ErrorMessageBox($"Patching error: { e.Message }");
                asm.Dispose();
                File.Delete(asmClonePath);
                return 1;
            }

            try
            {
                asm.Write(assemblyPath);
            }
            catch (Exception e)
            {
                ErrorMessageBox($"Unable to save patched game assembly to { assemblyPath }: { e.Message }");
                asm.Dispose();
                File.Delete(asmClonePath);
                return 1;
            }

            InfoMessageBox("Success! Don't forget to run patcher again when game updates.");

            mscorlibAsm.Dispose();
            unityEngineAsm.Dispose();
            unityEngineUIAsm.Dispose();
            asm.Dispose();

            File.Delete(asmClonePath);
            return 0;
        }
    }
}
