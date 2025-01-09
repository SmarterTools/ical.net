//
// Copyright ical.net project maintainers and contributors.
// Licensed under the MIT license.
//

#nullable enable
using System;
using System.Collections.Generic;
using System.Text;
using System.Text.RegularExpressions;

namespace Ical.Net.Serialization;

/// <summary>
/// Provides encoding and decoding services for byte arrays and strings.
/// </summary>
internal class EncodingProvider : IEncodingProvider
{
    private readonly SerializationContext _mSerializationContext;

    /// <summary>
    /// Represents a method that encodes a byte array into a string.
    /// </summary>
    public delegate string? EncoderDelegate(byte[] data);

    /// <summary>
    /// Represents a method that decodes a string into a byte array.
    /// </summary>
    public delegate byte[] DecoderDelegate(string value);

    /// <summary>
    /// Creates a new instance of the <see cref="EncodingProvider"/> class.
    /// </summary>
    /// <param name="ctx"></param>
    public EncodingProvider(SerializationContext ctx)
    {
        _mSerializationContext = ctx;
    }

    /// <summary>
    /// Decodes a 7-bit string into a byte array.
    /// </summary>
    /// <param name="value"></param>
    /// <returns>A byte array of the decoded string.</returns>
    protected byte[] Decode7Bit(string value)
    {
        try
        {
            var utf7 = new UTF7Encoding();
            return utf7.GetBytes(value);
        }
        catch
        {
            return null;
        }
    }

    /// <summary>
    /// Decodes an 8-bit string into a byte array.
    /// </summary>
    /// <param name="value"></param>
    /// <returns>A byte array of the decoded string.</returns>
    protected byte[] Decode8Bit(string value)
    {
        var utf8 = new UTF8Encoding();
        return utf8.GetBytes(value);
    }

    /// <summary>
    /// Decodes a base-64 encoded string into a byte array.
    /// </summary>
    /// <param name="value"></param>
    /// <returns>A byte array of the decoded string.</returns>
    protected byte[] DecodeBase64(string value)
    {
        return Convert.FromBase64String(value);
    }

    /// <summary>
    /// Decodes a quoted-printable encoded string into a byte array.
    /// </summary>
    /// <param name="value"></param>
    /// <returns>A byte array of the decoded string.</returns>
    protected byte[] DecodeQuotedPrintable(string value)
    {
        var outBuffer = new List<byte>();
        for (var i = 0; i < value.Length; i++)
        {
            if (value[i] != '=')
            {
                outBuffer.Add((byte) value[i]);
                continue;
            }

            var hex = value.Substring(i + 1, 2);
            i += 2;

            if (hex == "\r\n")
                continue;

            if (!isValidHex(hex))
            {
                /* Not wrapping or valid hex, just pass it through - when getting the value, other code tends to strip out the value, meaning this code parses:
                 *    <p>Normal   0         false   false   false                             Mic=rosoftInternetExplorer4</p>=0D=0A<br class=3D"clear" />
                 *
                 * When the source has:
                 *    <p>Normal   0         false   false   false                             Mic=
                 *     rosoftInternetExplorer4</p>=0D=0A<br class=3D"clear" />
                 *
                 * So we just want to drop the = and pass the other characters through.
                 */
                foreach (var c in hex)
                    outBuffer.Add((byte) c);
                continue;
            }

            var codePoint = Convert.ToInt32(hex, 16);
            outBuffer.Add(Convert.ToByte(codePoint));
        }

        return outBuffer.ToArray();
    }

    private static Regex hexRegex = new Regex("[0-9a-f]{2}", RegexOptions.Compiled | RegexOptions.IgnoreCase);
    private static bool isValidHex(string test)
    {
        return hexRegex.IsMatch(test);
    }

    /// <summary>
    /// Gets a decoder for the specified encoding.
    /// </summary>
    /// <param name="encoding"></param>
    /// <returns></returns>
    protected virtual DecoderDelegate? GetDecoderFor(string encoding)
    {
        return encoding.ToUpper() switch
        {
            "7BIT" => Decode7Bit,
            "8BIT" => Decode8Bit,
            "BASE64" => DecodeBase64,
            "QUOTED-PRINTABLE" => DecodeQuotedPrintable, // Not technically permitted under RFC, but GoToTraining uses it anyway, so let's play it safe.
            _ => null,
        };
    }

    /// <summary>
    /// Encodes a byte array into a 7-bit string.
    /// </summary>
    /// <param name="data"></param>
    /// <returns>A 7-bit string, if encoding is successful, else <see langword="null"/></returns>
    protected string Encode7Bit(byte[] data)
    {
        try
        {
            var utf7 = new UTF7Encoding();
            return utf7.GetString(data);
        }
        catch
        {
            return null;
        }
    }

    /// <summary>
    /// Encodes a byte array into an 8-bit string.
    /// </summary>
    /// <param name="data"></param>
    /// <returns>An 8-bit string, if encoding is successful, else <see langword="null"/></returns>
    protected string Encode8Bit(byte[] data)
    {
        var utf8 = new UTF8Encoding();
        return utf8.GetString(data);
    }

    /// <summary>
    /// Encodes a byte array into a base-64 encoded string.
    /// </summary>
    /// <param name="data"></param>
    /// <returns>A base-64 encoded string.</returns>
    protected string EncodeBase64(byte[] data)
    {
        return Convert.ToBase64String(data);
    }

    /// <summary>
    /// Encodes a byte array into a quoted-printable encoded string.
    /// </summary>
    /// <param name="data"></param>
    /// <returns>A quoted-printable encoded string.</returns>
    protected string EncodeQuotedPrintable(byte[] data)
    {
        var sb = new StringBuilder();
        foreach (var b in data)
        {
            if (b >= 33 && b <= 126 && b != 61)
            {
                sb.Append(Convert.ToChar(b));
                continue;
            }

            sb.Append($"={b:X2}");
        }

        return sb.ToString();
    }

    /// <summary>
    /// Gets an encoder for the specified encoding.
    /// </summary>
    /// <param name="encoding"></param>
    /// <returns></returns>
    protected virtual EncoderDelegate? GetEncoderFor(string encoding)
    {
        return encoding.ToUpper() switch
        {
            "7BIT" => Encode7Bit,
            "8BIT" => Encode8Bit,
            "BASE64" => EncodeBase64,
            "QUOTED-PRINTABLE" => EncodeQuotedPrintable, // Not technically permitted under RFC, but GoToTraining uses it anyway, so let's play it safe.
            _ => null
        };
    }

    /// <summary>
    /// Encodes a byte array into a string.
    /// </summary>
    /// <param name="encoding"></param>
    /// <param name="data"></param>
    /// <returns>A string representation of <paramref name="data"/> using the specified <see paramref="encoding"/>, or <see langword="null"/> if encoding fails.</returns>
    public string? Encode(string encoding, byte[] data)
    {
        var encoder = GetEncoderFor(encoding);
        return encoder?.Invoke(data);
    }

    /// <summary>
    /// Encodes a string into an encoded string by using the <see cref="EncodingStack"/> service.
    /// </summary>
    /// <param name="encoding"></param>
    /// <param name="value"></param>
    /// <returns>A string representation of <paramref name="value"/> using the specified <see paramref="encoding"/>.</returns>
    /// <exception cref="FormatException">Base64 string is invalid.</exception>
    public string? DecodeString(string encoding, string value)
    {
        var data = DecodeData(encoding, value);

        // Decode the string into the current encoding
        var encodingStack = _mSerializationContext.GetService(typeof(EncodingStack)) as EncodingStack;
        return data != null ? encodingStack?.Current.GetString(data) : null;
    }

    /// <summary>
    /// Decodes an encoded string into a byte array.
    /// </summary>
    /// <param name="encoding"></param>
    /// <param name="value"></param>
    /// <returns>A string representation of <paramref name="value"/> using the specified <see paramref="encoding"/>, or <see langword="null"/> when decoding fails.</returns>
    public byte[]? DecodeData(string encoding, string value)
    {
        var decoder = GetDecoderFor(encoding);
        return decoder?.Invoke(value);
    }
}
