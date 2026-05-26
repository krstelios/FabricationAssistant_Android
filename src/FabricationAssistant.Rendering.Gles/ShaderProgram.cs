using Silk.NET.OpenGLES;

namespace FabricationAssistant.Rendering.Gles;

/// <summary>
/// Minimal ES 3.1 shader program wrapper. Mirrors the desktop ShaderProgram
/// (in FabricationAssistant.Rendering.OpenTK) but uses Silk.NET ES bindings.
/// Plan 2 extends this with uniform binding helpers.
/// </summary>
public sealed class ShaderProgram : IDisposable
{
    private readonly GL _gl;
    private readonly Dictionary<string, int> _uniformLocations = new(StringComparer.Ordinal);

    public uint Handle { get; private set; }
    public string Name { get; }

    public ShaderProgram(GL gl, string name, string vertexSource, string fragmentSource)
    {
        _gl = gl ?? throw new ArgumentNullException(nameof(gl));
        Name = name ?? throw new ArgumentNullException(nameof(name));

        var vs = CompileShader(ShaderType.VertexShader, vertexSource, $"{name}.vert");
        uint fs;
        try
        {
            fs = CompileShader(ShaderType.FragmentShader, fragmentSource, $"{name}.frag");
        }
        catch
        {
            _gl.DeleteShader(vs);
            throw;
        }

        Handle = _gl.CreateProgram();
        _gl.AttachShader(Handle, vs);
        _gl.AttachShader(Handle, fs);
        _gl.LinkProgram(Handle);

        _gl.GetProgram(Handle, ProgramPropertyARB.LinkStatus, out int linked);
        if (linked == 0)
        {
            string log = _gl.GetProgramInfoLog(Handle);
            _gl.DetachShader(Handle, vs);
            _gl.DetachShader(Handle, fs);
            _gl.DeleteShader(vs);
            _gl.DeleteShader(fs);
            _gl.DeleteProgram(Handle);
            Handle = 0;
            throw new InvalidOperationException($"Program {name} link failed:\n{log}");
        }

        _gl.DetachShader(Handle, vs);
        _gl.DetachShader(Handle, fs);
        _gl.DeleteShader(vs);
        _gl.DeleteShader(fs);
    }

    private uint CompileShader(ShaderType type, string source, string label)
    {
        uint handle = _gl.CreateShader(type);
        _gl.ShaderSource(handle, source);
        _gl.CompileShader(handle);
        _gl.GetShader(handle, ShaderParameterName.CompileStatus, out int compiled);
        if (compiled == 0)
        {
            string log = _gl.GetShaderInfoLog(handle);
            _gl.DeleteShader(handle);
            throw new InvalidOperationException($"Shader {label} compile failed:\n{log}");
        }
        return handle;
    }

    public void Use() => _gl.UseProgram(Handle);

    public int UniformLocation(string name)
    {
        if (Handle == 0)
            return -1;

        if (_uniformLocations.TryGetValue(name, out int loc))
            return loc;

        loc = _gl.GetUniformLocation(Handle, name);
        _uniformLocations[name] = loc;
        return loc;
    }

    public int UniformArrayLocation(string name)
    {
        int loc = UniformLocation(name);
        return loc >= 0 ? loc : UniformLocation(name + "[0]");
    }

    public void ClearUniformCache()
        => _uniformLocations.Clear();

    public void Dispose()
    {
        if (Handle != 0)
        {
            _gl.DeleteProgram(Handle);
            Handle = 0;
        }
        _uniformLocations.Clear();
    }
}
