﻿using System;
using System.Collections.Generic;
using System.Drawing;
using System.IO;
using System.Windows.Media.Imaging;
using BExplorer.Shell.Interop;
using Size = System.Drawing.Size;

namespace BExplorer.Shell._Plugin_Interfaces {

	public interface IListItemEx : IEnumerable<IListItemEx>, IDisposable, IEquatable<IListItemEx>, IEqualityComparer<IListItemEx> {

		/// <summary>The COM interface for this item</summary>
		IShellItem ComInterface { get; }

		/// <summary>The logical parent for this item</summary>
		IListItemEx Parent { get; }

		/// <summary>The text that represents the display name</summary>
		String DisplayName { get; set; }

		/// <summary>The file system extension for this item</summary>
		String Extension { get; set; }

		/// <summary>The file system path</summary>
		String FileSystemPath { get; set; }

		Int32 ItemIndex { get; set; }

		/// <summary>I think we can remove this and just use this.Parent.PIDL. This property's role is confusing because of how its used</summary>
		IntPtr ParentHandle { get; set; }

		/// <summary>Does the current item need to be refreshed in the ShellListView</summary>
		Boolean IsNeedRefreshing { get; set; }

		/// <summary>Assigned values but never used</summary>
		Boolean IsInvalid { get; set; }

		/// <summary>Changes how the item gets loaded</summary>
		Boolean IsOnlyLowQuality { get; set; }


		Boolean IsThumbnailLoaded { get; set; }

		/// <summary>Set but never used</summary>
		Boolean IsInitialised { get; set; } //TODO: Use this property or delete it

		Int32 OverlayIconIndex { get; set; }

		Int32 GroupIndex { get; set; }

		/// <summary>Set but never used</summary>
		Int32 IconIndex { get; set; } //TODO: Use this property or delete it

		Size IconSize { get; set; }

		IExtractIconPWFlags IconType { get; set; }

		/// <summary>Set but never used</summary>
		IntPtr ILPidl { get; } //TODO: Use this property or delete it

		IntPtr PIDL { get; set; }

		/// <summary>Not Used</summary>
		IShellFolder IFolder { get; set; }  //TODO: Use this property or delete it

		/// <summary>Index of the ShieldedIcon</summary>
		Int32 ShieldedIconIndex { get; set; }

		/// <summary>Is this item's icon loaded yet?</summary>
		Boolean IsIconLoaded { get; set; }

		Boolean IsFileSystem { get; set; }

		Boolean IsNetworkPath { get; set; }

		/// <summary>Is current item represent a system drive?</summary>
		Boolean IsDrive { get; set; }

		/// <summary>Is the current item a search folder?</summary>
		Boolean IsSearchFolder { get; set; }

		/// <summary>Implemented but never used</summary>
		Bitmap Thumbnail(Int32 size, ShellThumbnailFormatOption format, ShellThumbnailRetrievalOption source); //TODO: Use or Delete

		/// <summary>Gets the item's BitmapSource</summary>
		BitmapSource ThumbnailSource(Int32 size, ShellThumbnailFormatOption format, ShellThumbnailRetrievalOption source);

		/// <summary>Implemented but never used</summary>
		BitmapSource BitmapSource { get; } //TODO: Use or Delete

		String ParsingName { get; set; }

		Boolean IsBrowsable { get; set; } //TODO: Use or Delete

		/// <summary>Is this a folder?</summary>
		Boolean IsFolder { get; set; }

		/// <summary>Does this have folders?</summary>
		Boolean HasSubFolders { get; }

		Boolean IsHidden { get; set; }

		Boolean IsLink { get; }

		Boolean IsShared { get; set; }

		/// <summary>We should remove this and use this.Parent.IsSearchFolder</summary>
		Boolean IsParentSearchFolder { get; set; }

		void Initialize(IntPtr lvHandle, String path);

		void Initialize(IntPtr lvHandle, String path, Int32 index);

		void Initialize(IntPtr lvHandle, IntPtr pidl, int index);

		void Initialize(IntPtr lvHandle, IntPtr pidl);

		void InitializeWithParent(ShellItem parent, IntPtr lvHandle, IntPtr pidl, int index);

		IListItemEx Clone(Boolean isHardCloning = false);

		PropVariant GetPropertyValue(PROPERTYKEY pkey, Type type);

		Dictionary<PROPERTYKEY, Object> ColumnValues { get; set; }

		/// <summary>
		/// Gets all the sub items
		/// </summary>
		/// <param name="isEnumHidden">Should we include the hidden items?</param>
		/// <returns></returns>
		IListItemEx[] GetSubItems(Boolean isEnumHidden); //TODO: Use or Delete

		IShellFolder GetIShellFolder();

		String ToolTipText { get; }

		IntPtr AbsolutePidl { get; }

		/// <summary>Returns drive information</summary>
		DriveInfo GetDriveInfo();

		Boolean IsRCWSet { get; set; } //TODO: Use or Delete

		Int32 RCWThread { get; set; } //TODO: Use or Delete

		HResult ExtractAndDrawThumbnail(IntPtr hdc, uint iconSize, out WTS_CACHEFLAGS flags, User32.RECT iconBounds, out bool retrieved, bool isHidden, bool isRefresh = false); //TODO: Use or Delete

		HResult NavigationStatus { get; set; }

		IntPtr GetHBitmap(int iconSize, bool isThumbnail, bool isForce = false);

		Boolean RefreshThumb(int iconSize, out WTS_CACHEFLAGS flags); //TODO: Use or Delete

		String GetDisplayName(SIGDN type);

		IExtractIconPWFlags GetShield();

		int GetSystemImageListIndex(IntPtr pidl, ShellIconType type, ShellIconFlags flags);

		int GetUniqueID();
		//Work On This and the TODOList

	}
}
