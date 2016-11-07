﻿using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Forms;
using BExplorer.Shell.Interop;
using BExplorer.Shell._Plugin_Interfaces;
using Microsoft.Win32;

namespace BExplorer.Shell {
	public class ImageListEx {

		public IntPtr Handle { get; set; }

		#region Private Members

		private Boolean _IsSupressedTumbGeneration;
		private Int32 _CurrentSize { get; set; }
		private ShellView _ShellViewEx { get; set; }
		private IImageList2 _IImageList { get; set; }
		private readonly ImageList _Extra = new ImageList(ImageListSize.ExtraLarge);
		private readonly ImageList _Jumbo = new ImageList(ImageListSize.Jumbo);
		private readonly ImageList _Large = new ImageList(ImageListSize.Large);
		private readonly ImageList _Small = new ImageList(ImageListSize.SystemSmall);
		private readonly Thread _IconCacheLoadingThread;
		private readonly Thread _IconLoadingThread;
		private readonly Thread _UpdateSubitemValuesThread;
		private readonly Thread _RedrawingThread;
		private readonly Thread _OverlaysLoadingThread;
		private readonly Int32 _ExeFallBackIndex;
		private Int32 _FolderFallBackIndex;
		private Int32 _DefaultFallBackIndex;
		private readonly Int32 _ShieldIconIndex;
		private readonly Int32 _SharedIconIndex;
		private readonly SyncQueue<Int32> _OverlayQueue = new SyncQueue<Int32>(); //3000
		private readonly SyncQueue<Int32> _ThumbnailsForCacheLoad = new SyncQueue<Int32>(); //5000
		private readonly SyncQueue<Int32> _IconsForRetreval = new SyncQueue<Int32>(); //3000
		private readonly SyncQueue<Int32> _RedrawQueue = new SyncQueue<Int32>();
		private readonly SyncQueue<Tuple<Int32, Int32, PROPERTYKEY>> _ItemsForSubitemsUpdate = new SyncQueue<Tuple<Int32, Int32, PROPERTYKEY>>(); //5000
		private readonly IListItemEx _VideAddornerWide;
		private readonly IListItemEx _VideAddorner;
		public ManualResetEvent ResetEvent = new ManualResetEvent(false);

		#endregion

		public ImageListEx(Int32 size) {
			var ptr = IntPtr.Zero;
			this._CurrentSize = size;
			ptr = ComCtl32.ImageList_Create(size, size, 0x00000020 | 0x00010000 | 0x00020000, 0, 1);
			this._IImageList = (IImageList2)Marshal.GetObjectForIUnknown(ptr);
			this.Handle = ptr;
			this._IconLoadingThread = new Thread(_IconsLoadingThreadRun) { IsBackground = true, Priority = ThreadPriority.BelowNormal };
			this._IconLoadingThread.SetApartmentState(ApartmentState.STA);
			this._IconLoadingThread.Start();
			this._IconCacheLoadingThread = new Thread(_IconCacheLoadingThreadRun) { IsBackground = true, Priority = ThreadPriority.BelowNormal };
			this._IconCacheLoadingThread.SetApartmentState(ApartmentState.STA);
			this._IconCacheLoadingThread.Start();
			this._OverlaysLoadingThread = new Thread(_OverlaysLoadingThreadRun) { IsBackground = true, Priority = ThreadPriority.BelowNormal };
			this._OverlaysLoadingThread.SetApartmentState(ApartmentState.STA);
			this._OverlaysLoadingThread.Start();
			this._UpdateSubitemValuesThread = new Thread(_UpdateSubitemValuesThreadRun) { Priority = ThreadPriority.BelowNormal };
			this._UpdateSubitemValuesThread.SetApartmentState(ApartmentState.STA);
			this._UpdateSubitemValuesThread.Start();
			this._RedrawingThread = new Thread(_RedrawingThreadRun) { IsBackground = true, Priority = ThreadPriority.Normal };
			this._RedrawingThread.SetApartmentState(ApartmentState.STA);
			this._RedrawingThread.Start();
			var defIconInfo = new Shell32.SHSTOCKICONINFO() { cbSize = (UInt32)Marshal.SizeOf(typeof(Shell32.SHSTOCKICONINFO)) };

			Shell32.SHGetStockIconInfo(Shell32.SHSTOCKICONID.SIID_APPLICATION, Shell32.SHGSI.SHGSI_SYSICONINDEX, ref defIconInfo);
			this._ExeFallBackIndex = defIconInfo.iSysIconIndex;
			Shell32.SHGetStockIconInfo(Shell32.SHSTOCKICONID.SIID_SHIELD, Shell32.SHGSI.SHGSI_SYSICONINDEX, ref defIconInfo);
			this._ShieldIconIndex = defIconInfo.iSysIconIndex;

			Shell32.SHGetStockIconInfo(Shell32.SHSTOCKICONID.SIID_SHARE, Shell32.SHGSI.SHGSI_SYSICONINDEX, ref defIconInfo);
			this._SharedIconIndex = defIconInfo.iSysIconIndex;

			Shell32.SHGetStockIconInfo(Shell32.SHSTOCKICONID.SIID_FOLDER, Shell32.SHGSI.SHGSI_SYSICONINDEX, ref defIconInfo);
			this._FolderFallBackIndex = defIconInfo.iSysIconIndex;

			Shell32.SHGetStockIconInfo(Shell32.SHSTOCKICONID.SIID_DOCNOASSOC, Shell32.SHGSI.SHGSI_SYSICONINDEX, ref defIconInfo);
			this._DefaultFallBackIndex = defIconInfo.iSysIconIndex;
			var basePath = Path.GetDirectoryName(Assembly.GetExecutingAssembly().Location);
			this._VideAddornerWide = FileSystemListItem.ToFileSystemItem(IntPtr.Zero,
				Path.Combine(basePath, "video_addorner_wide.png"));
			this._VideAddorner = FileSystemListItem.ToFileSystemItem(IntPtr.Zero,
				Path.Combine(basePath, "video_addorner.png"));
		}

		public void Dispose() {
			Thread t = new Thread(() => {
				if (this._IconLoadingThread.IsAlive)
					this._IconLoadingThread.Abort();
				if (this._IconCacheLoadingThread.IsAlive)
					this._IconCacheLoadingThread.Abort();
				if (this._OverlaysLoadingThread.IsAlive)
					this._OverlaysLoadingThread.Abort();
				if (this._UpdateSubitemValuesThread.IsAlive)
					this._UpdateSubitemValuesThread.Abort();
				if (this._RedrawingThread.IsAlive)
					this._RedrawingThread.Abort();
			});

			t.Start();
		}

		public void ReInitQueues() {
			this._RedrawQueue.Clear();
			this._ThumbnailsForCacheLoad.Clear();
			this._OverlayQueue.Clear();
			this._IconsForRetreval.Clear();
			this._ItemsForSubitemsUpdate.Clear();
		}

		public void EnqueueSubitemsGet(Tuple<int, int, PROPERTYKEY> item) {
			Task.Run(() => {
				this._ItemsForSubitemsUpdate.Enqueue(item);
			});
		}


		public void ResizeImages(Int32 newSize) {
			this.ReInitQueues();
			this._CurrentSize = newSize;
			this._IImageList.SetIconSize(newSize, newSize);
		}

		public void AttachToListView(ShellView listView, Int32 type) {
			this._ShellViewEx = listView;
			User32.SendMessage(listView.LVHandle, MSG.LVM_SETIMAGELIST, type, this.Handle);
		}

		public void SupressThumbnailGeneration(Boolean isSupress) {
			this._IsSupressedTumbGeneration = isSupress;
		}

		public void DrawIcon(IntPtr hdc, Int32 index, IListItemEx sho, User32.RECT iconBounds, Boolean isGhosted, Boolean isHot) {
			if (sho.OverlayIconIndex == -1) {
				Task.Run(() => {
					this._OverlayQueue.Enqueue(index);
				});
			}
			//TODO: Check why the same code is called 2 times here. It can be fixed by if (!this._RedrawQueue) {
			if (this._CurrentSize != 16) {
				var addornerType = this.GetAddornerType(sho);
				IntPtr hThumbnail = IntPtr.Zero;
				hThumbnail = sho.GetHBitmap(addornerType == 2 ? this._CurrentSize - 7 : this._CurrentSize, true);
				Int32 width = 0;
				Int32 height = 0;
				if (hThumbnail != IntPtr.Zero) {
					Gdi32.ConvertPixelByPixel(hThumbnail, out width, out height);
					Int32 width2 = 0;
					Int32 height2 = 0;
					if (addornerType > 0) {
						this.DrawWithAddorner(hdc, iconBounds, isGhosted, width, height, hThumbnail, addornerType);
					} else {
						Gdi32.NativeDraw(hdc, hThumbnail,
							iconBounds.Left + (iconBounds.Right - iconBounds.Left - width) / 2,
							iconBounds.Top + (iconBounds.Bottom - iconBounds.Top - height) / 2, width, height, isGhosted);
					}
					if (addornerType == 2) {
						width = width + 7;
						height = height + 7;
					}
					sho.IsNeedRefreshing = ((width > height && width != this._CurrentSize) ||
																	(width < height && height != this._CurrentSize) ||
																	(width == height && width != this._CurrentSize)) &&
																 !sho.IsOnlyLowQuality;
					if (sho.IsNeedRefreshing) {
						sho.IsThumbnailLoaded = false;
						Task.Run(() => {
							this._ThumbnailsForCacheLoad.Enqueue(index);
						});
					} else {
						sho.IsThumbnailLoaded = true;
						sho.IsNeedRefreshing = false;
					}

				} else {
					if (sho.IsIconLoaded ||
							(((sho.IconType & IExtractIconPWFlags.GIL_PERCLASS) == IExtractIconPWFlags.GIL_PERCLASS ||
								(!this._ShellViewEx.IsSearchNavigating &&
								 this._ShellViewEx.RequestedCurrentLocation.ParsingName.Equals(KnownFolders.Libraries.ParsingName,
									 StringComparison.InvariantCultureIgnoreCase)) ||
								(!this._ShellViewEx.IsSearchNavigating &&
								 this._ShellViewEx.RequestedCurrentLocation.ParsingName.Equals(KnownFolders.Computer.ParsingName,
									 StringComparison.InvariantCultureIgnoreCase))))) {
						hThumbnail = sho.GetHBitmap(this._CurrentSize, false);
						if (hThumbnail != IntPtr.Zero) {
							Gdi32.ConvertPixelByPixel(hThumbnail, out width, out height);
							Gdi32.NativeDraw(hdc, hThumbnail, iconBounds.Left + (iconBounds.Right - iconBounds.Left - width) / 2,
								iconBounds.Top + (iconBounds.Bottom - iconBounds.Top - height) / 2, width, height, isGhosted);
							sho.IsIconLoaded = true;
						} else {
							this.DrawDefaultIcons(hdc, sho, iconBounds);
							sho.IsThumbnailLoaded = true;
							sho.IsNeedRefreshing = false;
							sho.IsIconLoaded = false;
							//if (!this._IconsForRetreval.Contains(index))
							Task.Run(() => {
								this._IconsForRetreval.Enqueue(index);
							});
						}
						if (!sho.IsThumbnailLoaded || sho.IsNeedRefreshing)
							Task.Run(() => {
								this._ThumbnailsForCacheLoad.Enqueue(index);
							});
					} else {
						var editControl = User32.SendMessage(this._ShellViewEx.LVHandle, 0x1018, 0, 0);
						if (editControl == IntPtr.Zero) {
							this.DrawDefaultIcons(hdc, sho, iconBounds);
							sho.IsIconLoaded = false;
							//if (!this._IconsForRetreval.Contains(index))
							Task.Run(() => {
								this._IconsForRetreval.Enqueue(index);
							});
						} else {
							hThumbnail = sho.GetHBitmap(this._CurrentSize, false);
							if (hThumbnail != IntPtr.Zero) {
								Gdi32.ConvertPixelByPixel(hThumbnail, out width, out height);
								Gdi32.NativeDraw(hdc, hThumbnail, iconBounds.Left + (iconBounds.Right - iconBounds.Left - width) / 2,
									iconBounds.Top + (iconBounds.Bottom - iconBounds.Top - height) / 2, width, height, isGhosted);
							} else {
								this.DrawDefaultIcons(hdc, sho, iconBounds);
								sho.IsIconLoaded = false;
								//if (!this._IconsForRetreval.Contains(index))
								Task.Run(() => {
									this._IconsForRetreval.Enqueue(index);
								});
							}
							if ((sho.GetShield() & IExtractIconPWFlags.GIL_SHIELD) != 0) {
								sho.ShieldedIconIndex = this._ShieldIconIndex;
							}
						}
					}
				}
				using (var g = Graphics.FromHdc(hdc)) {
					if (this._ShellViewEx.ShowCheckboxes && this._ShellViewEx.View != ShellViewStyle.Details &&
							this._ShellViewEx.View != ShellViewStyle.List) {
						var lvi = new LVITEMINDEX();
						lvi.iItem = index;
						lvi.iGroup = this._ShellViewEx.GetGroupIndex(index);
						var lvItem = new LVITEM() {
							iItem = index,
							iGroupId = lvi.iGroup,
							iGroup = lvi.iGroup,
							mask = LVIF.LVIF_STATE,
							stateMask = LVIS.LVIS_SELECTED
						};
						var lvItemImageMask = new LVITEM() {
							iItem = index,
							iGroupId = lvi.iGroup,
							iGroup = lvi.iGroup,
							mask = LVIF.LVIF_STATE,
							stateMask = LVIS.LVIS_STATEIMAGEMASK
						};
						var res = User32.SendMessage(this._ShellViewEx.LVHandle, MSG.LVM_GETITEMW, 0, ref lvItemImageMask);

						if (isHot || (UInt32)lvItemImageMask.state == (2 << 12)) {
							res = User32.SendMessage(this._ShellViewEx.LVHandle, MSG.LVM_GETITEMW, 0, ref lvItem);
							var checkboxOffsetH = 14;
							var checkboxOffsetV = 2;
							if (this._ShellViewEx.View == ShellViewStyle.Tile || this._ShellViewEx.View == ShellViewStyle.SmallIcon)
								checkboxOffsetH = 2;
							if (this._ShellViewEx.View == ShellViewStyle.Tile)
								checkboxOffsetV = 5;

							CheckBoxRenderer.DrawCheckBox(g,
								new Point(iconBounds.Left + checkboxOffsetH, iconBounds.Top + checkboxOffsetV),
								lvItem.state != 0
									? System.Windows.Forms.VisualStyles.CheckBoxState.CheckedNormal
									: System.Windows.Forms.VisualStyles.CheckBoxState.UncheckedNormal
							);
						}
					}
				}
				if (sho.OverlayIconIndex > 0) {
					if (this._CurrentSize > 180)
						this._Jumbo.DrawOverlay(hdc, sho.OverlayIconIndex,
							new Point(iconBounds.Left,
								iconBounds.Bottom - (this._ShellViewEx.View == ShellViewStyle.Tile ? 5 : 0) - this._CurrentSize / 3),
							this._CurrentSize / 3);
					else if (this._CurrentSize > 64)
						this._Extra.DrawOverlay(hdc, sho.OverlayIconIndex,
							new Point(iconBounds.Left + 10 - (this._ShellViewEx.View == ShellViewStyle.Tile ? 5 : 0), iconBounds.Bottom - 50));
					else
						this._Large.DrawOverlay(hdc, sho.OverlayIconIndex,
							new Point(iconBounds.Left + 10 - (this._ShellViewEx.View == ShellViewStyle.Tile ? 5 : 0), iconBounds.Bottom - 32));
				}
				if (sho.ShieldedIconIndex > 0) {
					if (this._CurrentSize > 180)
						this._Jumbo.DrawIcon(hdc, sho.ShieldedIconIndex,
							new Point(iconBounds.Right - this._CurrentSize / 3 - 3, iconBounds.Bottom - this._CurrentSize / 3), this._CurrentSize / 3);
					else if (this._CurrentSize > 64)
						this._Extra.DrawIcon(hdc, sho.ShieldedIconIndex, new Point(iconBounds.Right - 43, iconBounds.Bottom - 50));
					else
						this._Large.DrawIcon(hdc, sho.ShieldedIconIndex, new Point(iconBounds.Right - 33, iconBounds.Bottom - 32));
				}
				if (sho.IsShared) {
					if (this._CurrentSize > 180)
						this._Jumbo.DrawIcon(hdc, this._SharedIconIndex,
							new Point(iconBounds.Right - this._CurrentSize / 3, iconBounds.Bottom - this._CurrentSize / 3), this._CurrentSize / 3);
					else if (this._CurrentSize > 64)
						this._Extra.DrawIcon(hdc, this._SharedIconIndex, new Point(iconBounds.Right - 40, iconBounds.Bottom - 50));
					else
						this._Large.DrawIcon(hdc, this._SharedIconIndex, new Point(iconBounds.Right - 30, iconBounds.Bottom - 32));
				}
				var badge = this.GetBadgeForPath(sho.ParsingName);
				if (badge != null) {
					var badgeIco = badge.GetHBitmap(this._CurrentSize, false);
					Gdi32.ConvertPixelByPixel(badgeIco, out width, out height);
					Gdi32.NativeDraw(hdc, badgeIco, iconBounds.Left + (iconBounds.Right - iconBounds.Left - _CurrentSize) / 2,
						iconBounds.Top + (iconBounds.Bottom - iconBounds.Top - _CurrentSize) / 2, _CurrentSize, isGhosted);
					Gdi32.DeleteObject(badgeIco);
				}

				if (this._ShellViewEx.View == ShellViewStyle.Tile) {
					var lvi = new LVITEMINDEX();
					lvi.iItem = index;
					lvi.iGroup = this._ShellViewEx.GetGroupIndex(index);
					var lableBounds = new User32.RECT() { Left = 2 };
					User32.SendMessage(this._ShellViewEx.LVHandle, MSG.LVM_GETITEMINDEXRECT, ref lvi, ref lableBounds);
					using (var g = Graphics.FromHdc(hdc)) {
						var fmt = new StringFormat();
						fmt.Trimming = StringTrimming.EllipsisCharacter;
						fmt.Alignment = StringAlignment.Center;
						fmt.Alignment = StringAlignment.Near;
						fmt.FormatFlags = StringFormatFlags.NoWrap | StringFormatFlags.FitBlackBox;
						fmt.LineAlignment = StringAlignment.Center;

						var lblrectTiles = new RectangleF(lableBounds.Left, iconBounds.Top + 6, lableBounds.Right - lableBounds.Left, 15);
						if (this._ShellViewEx.RequestedCurrentLocation.ParsingName.Equals(KnownFolders.Computer.ParsingName) &&
								(sho.IsDrive || sho.IsNetworkPath))
							this.DrawComputerTiledModeView(sho, g, lblrectTiles, fmt);
					}
				}
			} else {
				sho.IsThumbnailLoaded = true;
				Int32 width = 0;
				Int32 height = 0;
				if ((sho.IconType & IExtractIconPWFlags.GIL_PERCLASS) == IExtractIconPWFlags.GIL_PERCLASS) {
					var hIconExe = sho.GetHBitmap(this._CurrentSize, false);
					if (hIconExe != IntPtr.Zero) {
						sho.IsIconLoaded = true;
						Gdi32.ConvertPixelByPixel(hIconExe, out width, out height);
						Gdi32.NativeDraw(hdc, hIconExe, iconBounds.Left + (iconBounds.Right - iconBounds.Left - this._CurrentSize) / 2,
							iconBounds.Top + (iconBounds.Bottom - iconBounds.Top - this._CurrentSize) / 2, this._CurrentSize, isGhosted);
					}
				} else if ((sho.IconType & IExtractIconPWFlags.GIL_PERINSTANCE) == IExtractIconPWFlags.GIL_PERINSTANCE) {
					if (!sho.IsIconLoaded) {
						if (sho.IsNetworkPath || this._ShellViewEx.IsSearchNavigating) {
							Task.Run(() => {
								this._IconsForRetreval.Enqueue(index);
							});
						} else {
							Task.Run(() => {
								this._IconsForRetreval.Enqueue(index);
							});
						}
						this._Small.DrawIcon(hdc, this._ExeFallBackIndex,
							new Point(iconBounds.Left + (iconBounds.Right - iconBounds.Left - this._CurrentSize) / 2,
								iconBounds.Top + (iconBounds.Bottom - iconBounds.Top - this._CurrentSize) / 2));
					} else {
						var hIconExe = sho.GetHBitmap(this._CurrentSize, false);
						if (hIconExe != IntPtr.Zero) {
							sho.IsIconLoaded = true;
							Gdi32.ConvertPixelByPixel(hIconExe, out width, out height);
							Gdi32.NativeDraw(hdc, hIconExe, iconBounds.Left + (iconBounds.Right - iconBounds.Left - this._CurrentSize) / 2,
								iconBounds.Top + (iconBounds.Bottom - iconBounds.Top - this._CurrentSize) / 2, this._CurrentSize, isGhosted);
						}
					}
				}
				if (sho.OverlayIconIndex > 0) {
					this._Small.DrawOverlay(hdc, sho.OverlayIconIndex, new Point(iconBounds.Left, iconBounds.Bottom - 16));
				}
				if (sho.ShieldedIconIndex > 0) {
					this._Small.DrawIcon(hdc, sho.ShieldedIconIndex, new Point(iconBounds.Right - 9, iconBounds.Bottom - 10), 10);
				}
				if (sho.IsShared) {
					this._Small.DrawIcon(hdc, this._SharedIconIndex, new Point(iconBounds.Right - 9, iconBounds.Bottom - 16));
				}

				var badge = this.GetBadgeForPath(sho.ParsingName);
				if (badge != null) {
					var badgeIco = badge.GetHBitmap(16, false);
					Gdi32.ConvertPixelByPixel(badgeIco, out width, out height);
					Gdi32.NativeDraw(hdc, badgeIco, iconBounds.Left, iconBounds.Top, 16, isGhosted);
					Gdi32.DeleteObject(badgeIco);
				}
			}
		}

		private void DrawWithAddorner(IntPtr hdc, User32.RECT iconBounds, Boolean isGhosted, Int32 width, Int32 height, IntPtr hThumbnail, Int32 addornerType) {
			Int32 width2;
			Int32 height2;
			if (addornerType == 2) {
				var addorner = new Bitmap(width + 7, height + 7, PixelFormat.Format32bppPArgb);
				LinearGradientBrush gb = new LinearGradientBrush(new System.Drawing.Point(0, 0),
					new System.Drawing.Point(0, this._CurrentSize), Color.White, Color.WhiteSmoke);
				addorner = Gdi32.RoundCorners(addorner, 0, gb);
				gb.Dispose();
				var hAddorner = addorner.GetHbitmap();

				Gdi32.ConvertPixelByPixel(hAddorner, out width2, out height2);
				Gdi32.NativeDraw(hdc, hAddorner, iconBounds.Left + (iconBounds.Right - iconBounds.Left - width2) / 2,
					iconBounds.Top + (iconBounds.Bottom - iconBounds.Top - height2) / 2, width2, height2, isGhosted);
				Gdi32.DeleteObject(hAddorner);
				addorner.Dispose();
				Gdi32.NativeDraw(hdc, hThumbnail,
					iconBounds.Left + (iconBounds.Right - iconBounds.Left - width2) / 2 + 3,
					iconBounds.Top + (iconBounds.Bottom - iconBounds.Top - height2) / 2 + 3, width, height,
					isGhosted);
			} else if (addornerType == 3) {
				var isWide = (height / (Double)width) < 0.6;
				var hAddorner = isWide ? this._VideAddornerWide.GetHBitmap(this._CurrentSize, true, true) : this._VideAddorner.GetHBitmap(this._CurrentSize, true, true);
				Gdi32.ConvertPixelByPixel(hAddorner, out width2, out height2);
				if (isWide) {
					Gdi32.NativeDrawCrop(hdc, hThumbnail,
						iconBounds.Left + (iconBounds.Right - iconBounds.Left - width) / 2 + (Int32)(this._CurrentSize * 0.12),
						iconBounds.Top + (iconBounds.Bottom - iconBounds.Top - height) / 2 + (Int32)(this._CurrentSize * 0.07 / 2),
						(Int32)(this._CurrentSize * 0.12), (Int32)(this._CurrentSize * 0.03 / 2), width - (Int32)(this._CurrentSize * 0.13 * 2),
						 height - (Int32)(this._CurrentSize * 0.03),
						isGhosted);
					var left = iconBounds.Left + (iconBounds.Right - iconBounds.Left - width) / 2;
					var top = iconBounds.Top + (iconBounds.Bottom - iconBounds.Top - height) / 2;
					Gdi32.NativeDraw(hdc, hAddorner, left, top, width2, height2, width2, height + (Int32)(this._CurrentSize * 0.07) - (Int32)(this._CurrentSize * 0.04), isGhosted);
				} else {
					Gdi32.NativeDrawCrop(hdc, hThumbnail,
						iconBounds.Left + (iconBounds.Right - iconBounds.Left - width) / 2 + (Int32)(this._CurrentSize * 0.12),
						iconBounds.Top + (iconBounds.Bottom - iconBounds.Top - height) / 2 + (Int32)(this._CurrentSize * 0.07 / 2),
						(Int32)(this._CurrentSize * 0.12), (Int32)(this._CurrentSize * 0.03 / 2), width - (Int32)(this._CurrentSize * 0.13 * 1.9),
						height - (Int32)(this._CurrentSize * 0.07),
						isGhosted);
					Gdi32.NativeDraw(hdc, hAddorner, iconBounds.Left + (iconBounds.Right - iconBounds.Left - width) / 2,
						iconBounds.Top + (iconBounds.Bottom - iconBounds.Top - height) / 2, width2, height2, width2, height + (Int32)(this._CurrentSize * 0.06) - (Int32)(this._CurrentSize * 0.07), isGhosted);
				}
				Gdi32.DeleteObject(hAddorner);

			}
		}


		private void _OverlaysLoadingThreadRun() {
			while (true) {
				try {
					Int32 index = 0;
					if (!ThreadRun_Helper(this._OverlayQueue, false, ref index)) continue;
					var shoTemp = this._ShellViewEx.Items[index];
					var sho = FileSystemListItem.ToFileSystemItem(shoTemp.ParentHandle, shoTemp.PIDL);

					Int32 overlayIndex = 0;
					this._Small.GetIconIndexWithOverlay(sho.PIDL, out overlayIndex);
					shoTemp.OverlayIconIndex = overlayIndex;
					if (overlayIndex > 0) {
						if (!this._RedrawQueue.Contains(index))
							this._RedrawQueue.Enqueue(index);
					}
				} catch { }
			}
		}

		private void _IconsLoadingThreadRun() {
			while (true) {
				if (this._ShellViewEx != null && this._ShellViewEx.IsSearchNavigating) {
					Thread.Sleep(5);
					//F.Application.DoEvents();
				}
				var index = 0;
				if (!ThreadRun_Helper(this._IconsForRetreval, false, ref index)) continue;
				var sho = this._ShellViewEx.Items[index];
				if (!sho.IsIconLoaded) {
					try {
						var temp = FileSystemListItem.ToFileSystemItem(sho.ParentHandle, sho.ParsingName.ToShellParsingName());
						var icon = temp.GetHBitmap(this._CurrentSize, false, true);
						var shieldOverlay = 0;

						if (sho.ShieldedIconIndex == -1) {
							if ((temp.GetShield() & IExtractIconPWFlags.GIL_SHIELD) != 0) shieldOverlay = this._ShieldIconIndex;
							sho.ShieldedIconIndex = shieldOverlay;
						}

						if (icon != IntPtr.Zero || shieldOverlay > 0) {
							sho.IsIconLoaded = true;
							Gdi32.DeleteObject(icon);
							if (!this._RedrawQueue.Contains(index))
								this._RedrawQueue.Enqueue(index);
						}
						//Catch File not found exception since it happens if the file is laready deleted
					} catch (FileNotFoundException) { }
				}
			}
		}

		private void _IconCacheLoadingThreadRun() {
			while (true) {
				//if (resetEvent != null) resetEvent.WaitOne();
				var index = 0;
				if (!ThreadRun_Helper(this._ThumbnailsForCacheLoad, true, ref index)) continue;
				if (this._ThumbnailsForCacheLoad.Count() > 1 || !this._IsSupressedTumbGeneration) {
					var sho = this._ShellViewEx.Items[index];
					if (sho.IsNeedRefreshing | !sho.IsThumbnailLoaded) {
						var addornerType = this.GetAddornerType(sho);

						var result = sho.GetHBitmap(addornerType == 2 ? this._CurrentSize - 7 : this._CurrentSize, true, true);
						sho.IsThumbnailLoaded = true;
						sho.IsNeedRefreshing = false;
						if (result != IntPtr.Zero) {
							var width = 0;
							var height = 0;
							Gdi32.ConvertPixelByPixel(result, out width, out height);
							if (addornerType == 2) {
								width = width + 7;
								height = height + 7;
							}
							sho.IsOnlyLowQuality = (width > height && width != this._CurrentSize) ||
																		 (width < height && height != this._CurrentSize) ||
																		 (width == height && width != this._CurrentSize);

							if (!this._RedrawQueue.Contains(index))
								this._RedrawQueue.Enqueue(index);
							Gdi32.DeleteObject(result);
						}
					}
				}
			}
		}

		private void _RedrawingThreadRun() {
			while (true) {
				ResetEvent?.WaitOne();
				Application.DoEvents();
				var index = this._RedrawQueue.Dequeue();
				if (User32.SendMessage(this._ShellViewEx.LVHandle, Interop.MSG.LVM_ISITEMVISIBLE, index, 0) == IntPtr.Zero) continue;
				this._ShellViewEx.RedrawItem(index);
			}
		}

		private void _UpdateSubitemValuesThreadRun() {
			while (true) {
				var index = this._ItemsForSubitemsUpdate.Dequeue();
				if (User32.SendMessage(this._ShellViewEx.LVHandle, Interop.MSG.LVM_ISITEMVISIBLE, index.Item1, 0) != IntPtr.Zero) {
					var currentItem = this._ShellViewEx.Items[index.Item1];
					var temp = currentItem.Clone();
					var isi2 = (IShellItem2)temp.ComInterface;
					var pvar = new PropVariant();
					var pk = index.Item3;
					if (pk.fmtid == SystemProperties.DriveFreeSpace.fmtid && pk.pid == SystemProperties.DriveFreeSpace.pid) continue;
					var guid = new Guid(InterfaceGuids.IPropertyStore);
					IPropertyStore propStore = null;
					isi2.GetPropertyStore(GetPropertyStoreOptions.Default, ref guid, out propStore);
					if (propStore != null && propStore.GetValue(ref pk, pvar) == HResult.S_OK) {
						if (currentItem.ColumnValues.Keys.Count(s => s.fmtid == pk.fmtid && s.pid == pk.pid) == 0) {
							try {
								currentItem.ColumnValues.Add(pk, pvar.Value);
								if (!this._RedrawQueue.Contains(index.Item1))
									this._RedrawQueue.Enqueue(index.Item1);
							} catch {
								//TODO: Fix this try-catch!!!!!
							}
						}

						pvar.Dispose();
						Marshal.ReleaseComObject(propStore);
					}
					temp.Dispose();
				}
			}
		}

		private Boolean ThreadRun_Helper(SyncQueue<Int32> queue, Boolean useComplexCheck, ref Int32 index) {
			try {
				index = queue.Dequeue();
				if (index == null) {
					return false;
				} else {
					var result = User32.SendMessage(this._ShellViewEx.LVHandle, Interop.MSG.LVM_ISITEMVISIBLE, index, 0) != IntPtr.Zero;
					return result;
				}
			} catch {
				return false;
			}
		}

		private IListItemEx GetBadgeForPath(String path) {
			var allBadges = this._ShellViewEx.BadgesData.SelectMany(s => s.Value).ToArray();
			var foundBadge = allBadges.Where(w => w.Value.Count(c => c.ToLowerInvariant().Equals(path.ToLowerInvariant())) > 0).FirstOrDefault();
			return foundBadge.Equals(default(KeyValuePair<IListItemEx, List<String>>)) ? null : foundBadge.Key;
		}

		private void DrawComputerTiledModeView(IListItemEx sho, Graphics g, RectangleF lblrectTiles, StringFormat fmt) {
			var driveInfo = new DriveInfo(sho.ParsingName);
			if (driveInfo.IsReady) {
				ProgressBarRenderer.DrawHorizontalBar(g, new Rectangle((Int32)lblrectTiles.Left, (Int32)lblrectTiles.Bottom + 4, (Int32)lblrectTiles.Width - 10, 10));
				var fullProcent = (100 * (driveInfo.TotalSize - driveInfo.AvailableFreeSpace)) / driveInfo.TotalSize;
				var barWidth = (lblrectTiles.Width - 12) * fullProcent / 100;
				var rec = new Rectangle((Int32)lblrectTiles.Left + 1, (Int32)lblrectTiles.Bottom + 5, (Int32)barWidth, 8);
				var gradRec = new Rectangle(rec.Left, rec.Top - 1, rec.Width, rec.Height + 2);
				var criticalUsed = fullProcent >= 90;
				var warningUsed = fullProcent >= 75;
				var averageUsed = fullProcent >= 50;
				var brush = new LinearGradientBrush(gradRec,
												criticalUsed ? Color.FromArgb(255, 0, 0) : warningUsed ? Color.FromArgb(255, 224, 0) : averageUsed ? Color.FromArgb(0, 220, 255) : Color.FromArgb(199, 248, 165),
												criticalUsed ? Color.FromArgb(150, 0, 0) : warningUsed ? Color.FromArgb(255, 188, 0) : averageUsed ? Color.FromArgb(43, 84, 235) : Color.FromArgb(101, 247, 0),
												LinearGradientMode.Vertical);
				g.FillRectangle(brush, rec);
				brush.Dispose();
				var lblrectSubiTem3 = new RectangleF(lblrectTiles.Left, lblrectTiles.Bottom + 16, lblrectTiles.Width, 15);
				Font subItemFont = System.Drawing.SystemFonts.IconTitleFont;
				var subItemTextBrush = new SolidBrush(System.Drawing.SystemColors.ControlDarkDark);
				g.DrawString($"{ShlWapi.StrFormatByteSize(driveInfo.AvailableFreeSpace)} free of {ShlWapi.StrFormatByteSize(driveInfo.TotalSize)}",
												subItemFont, subItemTextBrush, lblrectSubiTem3, fmt);

				subItemFont.Dispose();
				subItemTextBrush.Dispose();
			}
		}

		private void DrawDefaultIcons(IntPtr hdc, IListItemEx sho, User32.RECT iconBounds) {
			if (this._CurrentSize == 16) {
				this._Small.DrawIcon(hdc, this._ExeFallBackIndex, new Point(iconBounds.Left + (iconBounds.Right - iconBounds.Left - this._CurrentSize) / 2, iconBounds.Top + (iconBounds.Bottom - iconBounds.Top - this._CurrentSize) / 2), this._CurrentSize);
			} else if (this._CurrentSize <= 48) {
				this._Extra.DrawIcon(hdc, this._ExeFallBackIndex, new Point(iconBounds.Left + (iconBounds.Right - iconBounds.Left - this._CurrentSize) / 2, iconBounds.Top + (iconBounds.Bottom - iconBounds.Top - this._CurrentSize) / 2), this._CurrentSize);
			} else if (this._CurrentSize <= 256) {
				this._Jumbo.DrawIcon(hdc, this._ExeFallBackIndex, new Point(iconBounds.Left + (iconBounds.Right - iconBounds.Left - this._CurrentSize) / 2, iconBounds.Top + (iconBounds.Bottom - iconBounds.Top - this._CurrentSize) / 2), this._CurrentSize);
			}
		}

		private Int32 GetAddornerType(IListItemEx sho) {
			var result = 0;
			var extension = sho.Extension;
			var extensionRootKey = Registry.ClassesRoot.OpenSubKey(extension);
			if (extensionRootKey != null) {
				var perceivedType = extensionRootKey.GetValue("PerceivedType");
				extensionRootKey.Close();
				if (perceivedType != null) {
					var perceiveTypeKey = Registry.ClassesRoot.OpenSubKey(perceivedType.ToString());
					if (perceiveTypeKey != null) {
						var threatment = perceiveTypeKey.GetValue("Treatment");
						if (threatment != null) {
							result = (Int32)threatment;
						}
						perceiveTypeKey.Close();
					} else {
						var systemFileAssociationsKey = Registry.ClassesRoot.OpenSubKey(@"SystemFileAssociations\" + perceivedType);
						if (systemFileAssociationsKey != null) {
							var threatment = systemFileAssociationsKey.GetValue("Treatment");
							if (threatment != null) {
								result = (Int32)threatment;
							}
							systemFileAssociationsKey.Close();
						}
					}
				}
			}
			return result;
		}

	}
}
