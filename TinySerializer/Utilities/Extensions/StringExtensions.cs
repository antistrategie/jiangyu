using System.Globalization;
using System.Text;

namespace TinySerializer.Utilities.Extensions {
    public static class StringExtensions {
        public static string ToTitleCase(this string input) {
            StringBuilder builder = new StringBuilder();
            
            for (int i = 0; i < input.Length; i++) {
                char current = input[i];
                
                if (current == '_' && i + 1 < input.Length) {
                    char next = input[i + 1];
                    
                    if (char.IsLower(next)) {
                        next = char.ToUpper(next, CultureInfo.InvariantCulture);
                    }
                    
                    builder.Append(next);
                    i++;
                } else {
                    builder.Append(current);
                }
            }
            
            return builder.ToString();
        }
    }
}