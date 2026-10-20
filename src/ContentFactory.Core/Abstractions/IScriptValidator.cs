using ContentFactory.Contracts;

namespace ContentFactory.Core.Abstractions;

public interface IScriptValidator
{
    ValidationReport Validate(ScriptDocument script, ProductSnapshot product);
}
