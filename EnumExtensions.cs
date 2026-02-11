using System.ComponentModel.DataAnnotations;
using System.Reflection;

namespace WebullAnalytics
{
	public static class EnumExtensions
	{
		public static string DisplayName(this Enum enumValue)
		{
			return enumValue.GetType()
							.GetMember(enumValue.ToString())
							.FirstOrDefault()
							?.GetCustomAttribute<DisplayAttribute>()
							?.GetName() ?? enumValue.ToString();
		}
	}
}
