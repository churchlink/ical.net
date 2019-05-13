using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.Serialization;
using System.Text;
using System.Text.RegularExpressions;
using Ical.Net.CalendarComponents;

namespace Ical.Net.Serialization
{
    public class SimpleDeserializer
    {
        internal SimpleDeserializer(
            DataTypeMapper dataTypeMapper,
            ISerializerFactory serializerFactory,
            CalendarComponentFactory componentFactory)
        {
            _dataTypeMapper = dataTypeMapper;
            _serializerFactory = serializerFactory;
            _componentFactory = componentFactory;
        }

        public static readonly SimpleDeserializer Default = new SimpleDeserializer(
            new DataTypeMapper(),
            new SerializerFactory(),
            new CalendarComponentFactory());

        private const string _nameGroup = "name";
        private const string _valueGroup = "value";
        private const string _paramNameGroup = "paramName";
        private const string _paramValueGroup = "paramValue";

        private static readonly Regex _contentLineRegex = new Regex(BuildContentLineRegex(), RegexOptions.Compiled);

        private readonly DataTypeMapper _dataTypeMapper;
        private readonly ISerializerFactory _serializerFactory;
        private readonly CalendarComponentFactory _componentFactory;

        private static string BuildContentLineRegex()
        {
            // name          = iana-token / x-name
            // iana-token    = 1*(ALPHA / DIGIT / "-")
            // x-name        = "X-" [vendorid "-"] 1*(ALPHA / DIGIT / "-")
            // vendorid      = 3*(ALPHA / DIGIT)
            // Add underscore to match behavior of bug 2033495
            const string identifier = "[-A-Za-z0-9_]+";

            // param-value   = paramtext / quoted-string
            // paramtext     = *SAFE-CHAR
            // quoted-string = DQUOTE *QSAFE-CHAR DQUOTE
            // QSAFE-CHAR    = WSP / %x21 / %x23-7E / NON-US-ASCII
            // ; Any character except CONTROL and DQUOTE
            // SAFE-CHAR     = WSP / %x21 / %x23-2B / %x2D-39 / %x3C-7E
            //               / NON-US-ASCII
            // ; Any character except CONTROL, DQUOTE, ";", ":", ","
            var paramValue = $"((?<{_paramValueGroup}>[^\\x00-\\x08\\x0A-\\x1F\\x7F\";:,]*)|\"(?<{_paramValueGroup}>[^\\x00-\\x08\\x0A-\\x1F\\x7F\"]*)\")";

            // param         = param-name "=" param-value *("," param-value)
            // param-name    = iana-token / x-name
            var paramName = $"(?<{_paramNameGroup}>{identifier})";
            var param = $"{paramName}={paramValue}(,{paramValue})*";

            // contentline   = name *(";" param ) ":" value CRLF
            var name = $"(?<{_nameGroup}>{identifier})";
            // value         = *VALUE-CHAR
            var value = $"(?<{_valueGroup}>[^\\x00-\\x08\\x0E-\\x1F\\x7F]*)";
            var contentLine = $"^{name}(;{param})*:{value}$";
            return contentLine;
        }

        int ___deserializeLastLineNumHit = -1;
        int ___deserializeLastLineNumHitOrig = -1;
        string ___deserializeLastLineHitStr;

        public string GetDeserializeLastLineExceptionMsg(string origExMsg)
        {
            int lnNum = ___deserializeLastLineNumHit;
            int lnNumOrig = ___deserializeLastLineNumHitOrig;
            string lastLnErr = ___deserializeLastLineHitStr;

            if (lnNum < 0)
                return null;

            string errorMsg = $@"Ical parse exception:
Line number: `{lnNumOrig}` (`{lnNum}` after line breaks removed)
Line: `{lastLnErr}`

Original exception message: `{origExMsg}`";
            return errorMsg;
        }

        public IEnumerable<ICalendarComponent> Deserialize(TextReader reader)
        {
            var context = new SerializationContext();
            var stack = new Stack<ICalendarComponent>();
            var current = default(ICalendarComponent);

            string[] lines;
            int linesCount;
            List<int> origLineNumbers = new List<int>();

            try
            {
                lines = GetContentLines(reader, origLineNumbers).ToArray();

                linesCount = lines?.Length ?? 0;

                int isShortLen = linesCount - origLineNumbers.Count;
                if (isShortLen > 0) // shouldn't happen! should == 0. but to not throw exception, fill with -1 for expected remainder
                    origLineNumbers.AddRange(Enumerable.Repeat(-1, isShortLen));
            }
            catch (Exception ex)
            {
                throw ex;
            }

            for (int i = 0; i < linesCount; i++)
            { // foreach (var contentLineString in GetContentLines(reader)) {

                string line = lines[i];
                ___deserializeLastLineNumHit = i;
                ___deserializeLastLineNumHitOrig = origLineNumbers[i];
                ___deserializeLastLineHitStr = line;

                CalendarProperty contentLine = ParseContentLine(context, line);

                if (string.Equals(contentLine.Name, "BEGIN", StringComparison.OrdinalIgnoreCase))
                {
                    stack.Push(current);
                    current = _componentFactory.Build((string)contentLine.Value);
                    SerializationUtil.OnDeserializing(current);
                }
                else
                {
                    if (current == null)
                    {
                        throw new SerializationException($"Expected 'BEGIN', found '{contentLine.Name}'");
                    }
                    if (string.Equals(contentLine.Name, "END", StringComparison.OrdinalIgnoreCase))
                    {
                        if (!string.Equals((string)contentLine.Value, current.Name, StringComparison.OrdinalIgnoreCase))
                        {
                            throw new SerializationException($"Expected 'END:{current.Name}', found 'END:{contentLine.Value}'");
                        }
                        SerializationUtil.OnDeserialized(current);
                        ICalendarComponent finished = current;
                        current = stack.Pop();
                        if (current == null)
                        {
                            yield return finished;
                        }
                        else
                        {
                            current.Children.Add(finished);
                        }
                    }
                    else
                    {
                        current.Properties.Add(contentLine);
                    }
                }
            }
            if (current != null)
            {
                throw new SerializationException($"Unclosed component {current.Name}");
            }

            // clear these if there was no exception
            ___deserializeLastLineNumHit = -1;
            ___deserializeLastLineHitStr = null;
        }

        private CalendarProperty ParseContentLine(SerializationContext context, string input)
        {
            var match = _contentLineRegex.Match(input);
            if (!match.Success)
            {
                throw new SerializationException($"Could not parse line: '{input}'");
            }
            var name = match.Groups[_nameGroup].Value;
            var value = match.Groups[_valueGroup].Value;
            var paramNames = match.Groups[_paramNameGroup].Captures;
            var paramValues = match.Groups[_paramValueGroup].Captures;

            var property = new CalendarProperty(name.ToUpperInvariant());
            context.Push(property);
            SetPropertyParameters(property, paramNames, paramValues);
            SetPropertyValue(context, property, value);
            context.Pop();
            return property;
        }

        private static void SetPropertyParameters(CalendarProperty property, CaptureCollection paramNames, CaptureCollection paramValues)
        {
            var paramValueIndex = 0;
            for (var paramNameIndex = 0; paramNameIndex < paramNames.Count; paramNameIndex++)
            {
                var paramName = paramNames[paramNameIndex].Value;
                var parameter = new CalendarParameter(paramName);
                var nextParamIndex = paramNameIndex + 1 < paramNames.Count ? paramNames[paramNameIndex + 1].Index : int.MaxValue;
                while (paramValueIndex < paramValues.Count && paramValues[paramValueIndex].Index < nextParamIndex)
                {
                    var paramValue = paramValues[paramValueIndex].Value;
                    parameter.AddValue(paramValue);
                    paramValueIndex++;
                }
                property.AddParameter(parameter);
            }
        }

        private void SetPropertyValue(SerializationContext context, CalendarProperty property, string value)
        {
            var type = _dataTypeMapper.GetPropertyMapping(property) ?? typeof(string);
            var serializer = (SerializerBase)_serializerFactory.Build(type, context);
            using (var valueReader = new StringReader(value))
            {
                var propertyValue = serializer.Deserialize(valueReader);
                var propertyValues = propertyValue as IEnumerable<string>;
                if (propertyValues != null)
                {
                    foreach (var singlePropertyValue in propertyValues)
                    {
                        property.AddValue(singlePropertyValue);
                    }
                }
                else
                {
                    property.AddValue(propertyValue);
                }
            }
        }

        public static IEnumerable<string> GetContentLines(
            TextReader reader,
            List<int> origLineNumbers)
        {
            var sbCurrLine = new StringBuilder();

            int i = -1;
            while (true)
            {
                i++;
                string nextLine = reader.ReadLine();
                if (nextLine == null)
                    break;

                if (nextLine.Length <= 0)
                    continue;

                char firstCh = nextLine[0];

                if (firstCh == ' ' || firstCh == '\t')
                { // if first char is a space or a tab
                    sbCurrLine.Append(nextLine, 1, nextLine.Length - 1);
                }
                else
                {
                    if (sbCurrLine.Length > 0)
                    {
                        yield return sbCurrLine.ToString();
                    }
                    origLineNumbers.Add(i);
                    sbCurrLine.Clear();
                    sbCurrLine.Append(nextLine);
                }
            }
            if (sbCurrLine.Length > 0)
            {
                yield return sbCurrLine.ToString();
            }
        }
    }
}
