namespace SmartWord.AddIn
{
    // 文件说明：
    // 为 VSTO 自动生成的 Ribbon 集合提供强类型访问入口，减少调用处的类型转换与字符串依赖。
    internal sealed partial class ThisRibbonCollection
    {
        /// <summary>
        /// 获取 SmartWord Ribbon 的强类型实例。
        /// </summary>
        internal UI.SmartWordRibbon SmartWordRibbon
        {
            get { return this.GetRibbon<UI.SmartWordRibbon>(); }
        }
    }
}
