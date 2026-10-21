$ErrorActionPreference = 'Stop'
# Regenerate the checked-in PS 2.0 resource from its adjacent HLSL source. No SDK/tool installation.
Add-Type -TypeDefinition @'
using System;
using System.IO;
using System.Runtime.InteropServices;
public static class FpsColorShaderCompiler {
 [ComImport, Guid("8BA5FB08-5195-40e2-AC58-0D989C3A0102"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
 interface Blob { [PreserveSig] IntPtr GetBufferPointer(); [PreserveSig] UIntPtr GetBufferSize(); }
 [DllImport("d3dcompiler_47.dll", CallingConvention=CallingConvention.StdCall)]
 static extern int D3DCompile(byte[] data, UIntPtr length, [MarshalAs(UnmanagedType.LPStr)] string name,
  IntPtr defines, IntPtr include, [MarshalAs(UnmanagedType.LPStr)] string entry,
  [MarshalAs(UnmanagedType.LPStr)] string target, uint flags, uint flags2, out Blob code, out Blob errors);
 public static void Compile(string source, string destination) {
  var bytes=File.ReadAllBytes(source); Blob code=null, errors=null;
  try {
   int status=D3DCompile(bytes,(UIntPtr)bytes.Length,source,IntPtr.Zero,IntPtr.Zero,"main","ps_2_0",1u<<15,0,out code,out errors);
   if(status<0) throw new InvalidOperationException(errors==null ? "Shader compilation failed" : Marshal.PtrToStringAnsi(errors.GetBufferPointer()));
   var result=new byte[(int)code.GetBufferSize().ToUInt64()]; Marshal.Copy(code.GetBufferPointer(),result,0,result.Length);
   File.WriteAllBytes(destination,result);
  } finally {if(code!=null)Marshal.ReleaseComObject(code);if(errors!=null)Marshal.ReleaseComObject(errors);}
 }
}
'@
$directory = Join-Path $PSScriptRoot '../FpsTune.Wpf/Assets/Shaders'
[FpsColorShaderCompiler]::Compile((Join-Path $directory 'ColorPreview.hlsl'), (Join-Path $directory 'ColorPreview.ps'))
