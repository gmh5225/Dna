﻿using Dna.Binary.Windows;
using Dna.ControlFlow;
using Dna.ControlFlow.Analysis;
using Dna.Emulation;
using Dna.Lifting;
using Dna.Optimization;
using Dna.Optimization.Passes;
using Dna.Relocation;
using Dna.Synthesis.Jit;
using Dna.Synthesis.Miasm;
using Dna.Synthesis.Parsing;
using Dna.Synthesis.Simplification;
using Dna.Synthesis.Utils;
using DotNetGraph.Extensions;
using Rivers;
using Rivers.Analysis;
using System.Diagnostics;
using Grpc.Net.Client;
using TritonTranslator.Arch;
using TritonTranslator.Arch.X86;
using ClangSharp.Interop;
using ClangSharp;
using Dna.Decompiler;
using Dna.Emulation.Unicorn;
using Dna.Decompilation;
using Dna.Structuring.Stackify;
// Load the 64 bit PE file.
// Note: This file is automatically copied to the build directory.
var path = @"C:\Users\colton\Downloads\sample1.vmp.bin";
var binary = new LinuxBinary(64, File.ReadAllBytes(path), 0x400000);

// Instantiate dna.
var dna = new Dna.Dna(binary);

// Parse a (virtualized) control flow graph from the binary.
ulong funcAddr = 0x401146;
var cfg = dna.RecursiveDescent.ReconstructCfg(funcAddr);

/*
// The VM entry spans across multiple routines. To avoid disassembling multiple
// control flow graphs, we selectively insert instructions needed to have a
// correct CFG for the entirety of the vm entry.
var target = cfg.GetBlocks().First();
target.Instructions.Insert(0, dna.BinaryDisassembler.GetInstructionAt(0x14000177E));
target.Instructions.Insert(1, dna.BinaryDisassembler.GetInstructionAt(0x140001783));
target.Instructions.Insert(2, dna.BinaryDisassembler.GetInstructionAt(0x140001789));
target.Instructions.Insert(3, dna.BinaryDisassembler.GetInstructionAt(0x14000178F));
target.Instructions.Insert(4, dna.BinaryDisassembler.GetInstructionAt(0x140001794)); 
target.Instructions.Insert(5, dna.BinaryDisassembler.GetInstructionAt(0x140002CA0));
target.Instructions.Insert(6, dna.BinaryDisassembler.GetInstructionAt(0x140002CA5));
target.Instructions.Insert(7, dna.BinaryDisassembler.GetInstructionAt(0x140002CAA));
target.Instructions.Insert(8, dna.BinaryDisassembler.GetInstructionAt(0x140002CAE));
target.Instructions.Insert(9, dna.BinaryDisassembler.GetInstructionAt(0x140002CB2));
target.Instructions.Insert(10, dna.BinaryDisassembler.GetInstructionAt(0x140002CB6));
target.Instructions.Insert(11, dna.BinaryDisassembler.GetInstructionAt(0x140019225));
target.Instructions.Insert(12, dna.BinaryDisassembler.GetInstructionAt(0x14001922A));
*/

// Print the disassembled control flow graph.
var prompt = () =>
{
    Console.WriteLine("Press enter to continue...");
    //Console.ReadLine();
};
Console.WriteLine("Disassembled cfg:\n{0}", GraphFormatter.FormatGraph(cfg));
Console.WriteLine(GraphFormatter.FormatGraph(cfg));
prompt();

// Instantiate the cpu architecture.
var architecture = new X86CpuArchitecture(ArchitectureId.ARCH_X86_64);

// Instantiate a class for lifting control flow graphs to our intermediate language.
var cfgLifter = new CfgLifter(architecture);

// Lift the control flow graph to TTIR.
var liftedCfg = cfgLifter.LiftCfg(cfg);

for (int i = 0; i < 3; i++)
    Console.WriteLine("");

// Elminate deadcode from the control flow graph.
bool dce = false;
if (dce)
{
    var blockDcePass = new BlockDcePass(liftedCfg);
    blockDcePass.Run();
}

// Print the optimized control flow graph.
Console.WriteLine("Lifted cfg:\n{0}", GraphFormatter.FormatGraph(liftedCfg));
prompt();

// Create a .DOT file for visualizing the IR cfg.
var dotGraph = GraphVisualizer.GetDotGraph(liftedCfg);
File.WriteAllText("graph.dot", dotGraph.Compile(false, false));

bool emulate = true;

if (emulate)
{
    // Load the binary into unicorn engine.
    var emulator = new UnicornEmulator(architecture);
    BinaryMapper.MapELFFile(emulator, binary);

    ulong tebBase = 0x7fded000;
    ulong tebSize = 0x10000;
    emulator.MapMemory(tebBase, (int)tebSize);

    //emulator.MapMemory(0, (int)tebSize * 0x1000);

    //var igt = new GdtHelper(emulator.Emulator, 0x0, 0x1000);
    //igt.Setup(tebBase);

    // Setup the stack.
    ulong rsp = 0x100000000;
    emulator.MapMemory(rsp, 0x1000 * 1200);
    rsp += 0x20000;

    ulong pebAddr = 0x9A67F99120;
    emulator.MapMemory(pebAddr - 0x120, 0x1000);
    UInt16 val = 0x4A63;
    emulator.WriteMemory(pebAddr, BitConverter.GetBytes(val));


    int theSize = 0x1000 * 10;
    emulator.MapMemory(0, 0x1000);
    for (int i = 0; i < theSize; i++)
    {
        // emulator.WriteMemory((ulong)i, new byte[] { 0xFF });
    }
    //ulong gs = 0x1000;
    //emulator.MapMemory(0, 0x1000 * 1000);
    //emulator.SetRegister(register_e.ID_REG_X86_GS, 0);
    //emulator.SetRegister(register_e.ID_REG_X86_FS, 0);

    emulator.SetRegister(register_e.ID_REG_X86_RSP, rsp);
    emulator.SetRegister(register_e.ID_REG_X86_RBP, rsp);
    //emulator.SetRegister(register_e.ID_REG_X86_RIP, 0x14000177E);

    // Execute the function.
    emulator.Start(0x401194);
}


Console.WriteLine("Started");
Thread.Sleep(100000);


// Lift the control flow graph to LLVM IR.
var llvmLifter = new LLVMLifter(architecture);
llvmLifter.Lift(liftedCfg);
llvmLifter.Module.PrintToFile(@"lifted.ll");

bool optimize = true;
if (optimize)
{
    var passManager = llvmLifter.Module.CreateFunctionPassManager();
    passManager.AddBasicAliasAnalysisPass();
    passManager.AddTypeBasedAliasAnalysisPass();
    passManager.AddScopedNoAliasAAPass();
    passManager.AddLowerExpectIntrinsicPass();
    passManager.AddCFGSimplificationPass();
    passManager.AddPromoteMemoryToRegisterPass();
    passManager.AddEarlyCSEPass();
    passManager.AddDCEPass();
    passManager.AddAggressiveDCEPass();
    passManager.AddDeadStoreEliminationPass();
    passManager.AddInstructionCombiningPass();
    passManager.AddCFGSimplificationPass();
    passManager.AddDeadStoreEliminationPass();
    passManager.AddAggressiveDCEPass();
    passManager.InitializeFunctionPassManager();
    for (int i = 0; i < 10; i++)
    {
        passManager.RunFunctionPassManager(llvmLifter.llvmFunction);
    }

    passManager.FinalizeFunctionPassManager();
}

// Optionally write the llvm IR to the console.
bool printLLVM = true;
if (printLLVM)
    llvmLifter.Module.Dump();
prompt();

// Optionally decompile the lifted function to go-to free pseudo C, via Rellic.
// On my machine, a fork of Rellic runs under WSL2 and communiucates via gRPC.
// If you are not hosting this server at localhost:50051, then the API
// call will fail. You can find the service here(https://github.com/Colton1skees/rellic-api),
// although it will take a bit of leg work for outside use.
bool decompile = false;
if (decompile)
{
    // Create a decompiler instance.
    var decompiler = new Decompiler(architecture);

    // Decompile the lifted function to pseudo C.
    var ast = decompiler.Decompile(llvmLifter.Module);

    // Print the decompiled routine.
    Console.WriteLine("Decompiled routine:\n{0}", ast);
}

Console.WriteLine("Finished.");
Console.ReadLine();
